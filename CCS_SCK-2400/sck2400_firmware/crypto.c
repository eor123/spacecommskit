/*
 * crypto.c
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   AES-128 CCM per-packet encryption and authentication.
 *   Pure software implementation -- no TI hardware accelerator.
 *
 * Why software AES?
 *   The TI AESCCM hardware driver (AESCCMCC26XX) uses a shared semaphore
 *   (CryptoResourceCC26XX_accessSemaphore) that cannot safely be pended
 *   from uart_task context while rfTask holds the RF driver power domain.
 *   The hardware also produced keystream output that did not match .NET
 *   AesCcm for identical inputs. Software AES eliminates both issues:
 *   no semaphore, no power domain dependency, runs from any task context,
 *   and matches .NET AesCcm output exactly (both implement RFC 3610).
 *
 * How CCM works in this implementation:
 *   1. CCSDS header (6 bytes) is used as AAD (authenticated, not encrypted).
 *      The AAD bytes [4:5] are the UPDATED data_length field -- matching
 *      exactly what the C# ground station passes to AesCcm.Encrypt().
 *   2. A 13-byte nonce that never repeats for a given key (11/2026):
 *      [0..1] sender board APID, [2..5] sender boot count (ext flash,
 *      boot_counter.c), [6..9] per-boot message counter, [10] format
 *      version 0x01, [11..12] CCSDS seq word (informational).
 *      Before 11/2026 the nonce was only the CCSDS seq word, which repeats
 *      every ground-app session and wraps every 16384 packets.
 *   3. CBC-MAC authenticates [flags_block][AAD block][plaintext blocks].
 *   4. CTR mode encrypts the plaintext, producing ciphertext.
 *   5. CTR mode encrypts the CBC-MAC output to produce the 8-byte MAC tag.
 *
 * Wire format on air:
 *   [CCSDS Header 6B][Nonce 13B][Ciphertext N bytes][MAC 8B]
 *
 * Key management:
 *   Outgoing: always Key 0.
 *   Incoming: try Key 0, Key 1, Key 2 -- accept first valid MAC.
 *
 * Verified test vector (Key=0xFF*16, Nonce=0x00*11+0xC0+0x07,
 *   AAD=18 11 C0 07 00 15, Plaintext=0x01):
 *   Ciphertext: 0x83   MAC: 85 84 6C 8D 8C 2A 0B 19
 *   Matches .NET AesCcm and Python cryptography library exactly.
 *
 * Target:    CC1352P1F3RGZ / CC2652P1FRGZ
 * Toolchain: TI Clang + TI-RTOS7
 * Reference: RFC 3610 -- Counter with CBC-MAC (CCM)
 *            NIST SP 800-38C
 */

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include "crypto.h"
#include "security.h"   /* SCK_AES_KEY_0, SCK_AES_KEY_1, SCK_AES_KEY_2 */

#include <stdint.h>
#include <stdbool.h>
#include <string.h>

/* -----------------------------------------------------------------------
 * CCSDS header length (must match ccsds.h)
 * --------------------------------------------------------------------- */
#define CRYPTO_CCSDS_HDR_LEN    6

/* -----------------------------------------------------------------------
 * Key material
 * --------------------------------------------------------------------- */
static const uint8_t sKeys[SCK_CRYPTO_KEY_COUNT][SCK_CRYPTO_KEY_LEN] = {
    SCK_AES_KEY_0,
    SCK_AES_KEY_1,
    SCK_AES_KEY_2,
};

/* -----------------------------------------------------------------------
 * Module state
 * --------------------------------------------------------------------- */
static bool     sCryptoReady = false;

/* Nonce identity of THIS board (set once per boot by crypto_set_identity) */
static uint16_t sSelfApid    = 0;
static uint32_t sBootCount   = 0;
static uint32_t sTxCounter   = 0;   /* per-boot message counter, first nonce uses 1 */

/* Replay table: highest (boot, counter) accepted from each sender APID.
 * RAM only -- a reboot of THIS board clears it, which is safe because
 * every peer's counters only ever move forward.
 * Written only by the task that calls crypto_decrypt() (rfTask on both
 * GS and remote boards). Other tasks request a clear via sClearRequest. */
typedef struct
{
    bool     used;
    uint16_t apid;
    uint32_t boot;
    uint32_t ctr;
    uint32_t stamp;     /* LRU stamp for eviction */
} replay_entry_t;

static replay_entry_t    sReplay[SCK_CRYPTO_REPLAY_SLOTS];
static uint32_t          sReplayStamp   = 0;
static volatile bool     sClearRequest  = false;
static volatile uint32_t sIdleSeconds   = 0;   /* since last accepted packet */

static volatile crypto_stats_t sStats;

/* ═══════════════════════════════════════════════════════════════════════
 * SOFTWARE AES-128 BLOCK CIPHER
 *
 * Standard FIPS 197 AES-128 implementation.
 * Single block encrypt only -- CCM only needs encrypt direction.
 * Optimized for code size over speed (appropriate for CC1352P at 48MHz
 * with low packet rates).
 * ═════════════════════════════════════════════════════════════════════*/

/* AES S-box */
static const uint8_t sSbox[256] = {
    0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
    0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
    0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
    0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
    0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
    0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
    0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
    0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
    0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
    0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
    0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
    0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
    0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
    0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
    0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
    0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16,
};

/* AES round constants */
static const uint8_t sRcon[11] = {
    0x00,0x01,0x02,0x04,0x08,0x10,0x20,0x40,0x80,0x1b,0x36
};

/* xtime: multiply by 2 in GF(2^8) */
static inline uint8_t xtime(uint8_t x)
{
    return (uint8_t)((x << 1) ^ ((x >> 7) ? 0x1b : 0x00));
}

/* AES-128 key schedule: expand 16-byte key into 11 * 16-byte round keys */
static void aes_key_expand(const uint8_t key[16], uint8_t rk[11][16])
{
    memcpy(rk[0], key, 16);
    for (int i = 1; i <= 10; i++)
    {
        uint8_t *prev = rk[i-1];
        uint8_t *cur  = rk[i];
        /* RotWord + SubWord + Rcon on first word */
        cur[0]  = sSbox[prev[13]] ^ prev[0]  ^ sRcon[i];
        cur[1]  = sSbox[prev[14]] ^ prev[1];
        cur[2]  = sSbox[prev[15]] ^ prev[2];
        cur[3]  = sSbox[prev[12]] ^ prev[3];
        /* remaining 3 words */
        for (int j = 4; j < 16; j++)
            cur[j] = prev[j] ^ cur[j-4];
    }
}

/* MixColumns on a single column (4 bytes) */
static void mix_col(uint8_t s[4])
{
    uint8_t a = s[0], b = s[1], c = s[2], d = s[3];
    uint8_t t = a ^ b ^ c ^ d;
    s[0] ^= t ^ xtime(a ^ b);
    s[1] ^= t ^ xtime(b ^ c);
    s[2] ^= t ^ xtime(c ^ d);
    s[3] ^= t ^ xtime(d ^ a);
}

/*
 * aes128_encrypt_block()
 * Encrypt a single 16-byte block in place using pre-expanded round keys.
 */
static void aes128_encrypt_block(uint8_t blk[16], const uint8_t rk[11][16])
{
    /* AddRoundKey 0 */
    for (int i = 0; i < 16; i++) blk[i] ^= rk[0][i];

    for (int round = 1; round <= 10; round++)
    {
        /* SubBytes */
        for (int i = 0; i < 16; i++) blk[i] = sSbox[blk[i]];

        /* ShiftRows (column-major AES state: state[col*4 + row])
         * Row 0: bytes  0, 4, 8,12 -- no shift
         * Row 1: bytes  1, 5, 9,13 -- shift left 1: [1,5,9,13]->[5,9,13,1]
         * Row 2: bytes  2, 6,10,14 -- shift left 2: [2,6,10,14]->[10,14,2,6]
         * Row 3: bytes  3, 7,11,15 -- shift left 3: [3,7,11,15]->[15,3,7,11] */
        uint8_t t;
        /* Row 1 */
        t=blk[1]; blk[1]=blk[5]; blk[5]=blk[9]; blk[9]=blk[13]; blk[13]=t;
        /* Row 2 */
        t=blk[2]; blk[2]=blk[10]; blk[10]=t;
        t=blk[6]; blk[6]=blk[14]; blk[14]=t;
        /* Row 3 */
        t=blk[3]; blk[3]=blk[15]; blk[15]=blk[11]; blk[11]=blk[7]; blk[7]=t;

        /* MixColumns (skip in final round) */
        if (round < 10)
        {
            for (int col = 0; col < 4; col++)
                mix_col(&blk[col * 4]);
        }

        /* AddRoundKey */
        for (int i = 0; i < 16; i++) blk[i] ^= rk[round][i];
    }
}

/* ═══════════════════════════════════════════════════════════════════════
 * CCM MODE (RFC 3610)
 *
 * Parameters fixed for SCK-2400:
 *   L = 2  (length field size, since N = 13 and L + N = 15)
 *   N = 13 (nonce length)
 *   M = 8  (MAC tag length)
 * ═════════════════════════════════════════════════════════════════════*/

/*
 * ccm_cbc_mac()
 * Compute CBC-MAC over AAD and plaintext for CCM authentication.
 * Produces a 16-byte T value; caller takes first SCK_CRYPTO_MAC_LEN bytes.
 *
 * RFC 3610 Section 2.2 formatting:
 *   B_0 = flags | nonce | Q (message length, 2 bytes for L=2)
 *   B_1 = AAD length encoding | AAD bytes | padding
 *   B_2..B_n = plaintext blocks (zero-padded)
 */
static void ccm_cbc_mac(const uint8_t rk[11][16],
                         const uint8_t *nonce,   uint8_t nonce_len,
                         const uint8_t *aad,     uint16_t aad_len,
                         const uint8_t *msg,     uint16_t msg_len,
                         uint8_t        T[16])
{
    /* B_0: flags | nonce | msg_len */
    uint8_t B[16];
    memset(B, 0, 16);
    /* flags = 64*(aad_len>0) + 8*((M-2)/2) + (L-1)
     * M=8: (8-2)/2 = 3  → 8*3=24
     * L=2: L-1 = 1
     * aad present: 64
     * flags = 64 + 24 + 1 = 0x59 */
    B[0] = (aad_len > 0 ? 0x40 : 0x00) | 0x18 | 0x01;  /* 0x59 */
    memcpy(&B[1], nonce, nonce_len);                      /* B[1..13] = nonce */
    B[14] = (uint8_t)(msg_len >> 8);                     /* Q high byte */
    B[15] = (uint8_t)(msg_len & 0xFF);                   /* Q low byte  */

    /* T = AES(B_0) */
    memcpy(T, B, 16);
    aes128_encrypt_block(T, rk);

    /* B_1: AAD length (2 bytes for len < 0xFF00) | AAD | zero-pad to 16 */
    if (aad_len > 0)
    {
        uint8_t aad_block[16];
        memset(aad_block, 0, 16);
        aad_block[0] = (uint8_t)(aad_len >> 8);
        aad_block[1] = (uint8_t)(aad_len & 0xFF);
        uint16_t copy = (aad_len > 14) ? 14 : aad_len;
        memcpy(&aad_block[2], aad, copy);
        for (int i = 0; i < 16; i++) T[i] ^= aad_block[i];
        aes128_encrypt_block(T, rk);

        /* remaining AAD bytes (if aad_len > 14), in 16-byte blocks */
        uint16_t done = copy;
        while (done < aad_len)
        {
            memset(aad_block, 0, 16);
            uint16_t chunk = (aad_len - done > 16) ? 16 : (aad_len - done);
            memcpy(aad_block, aad + done, chunk);
            for (int i = 0; i < 16; i++) T[i] ^= aad_block[i];
            aes128_encrypt_block(T, rk);
            done += chunk;
        }
    }

    /* Message blocks */
    uint16_t done = 0;
    while (done < msg_len)
    {
        uint8_t mblk[16];
        memset(mblk, 0, 16);
        uint16_t chunk = (msg_len - done > 16) ? 16 : (msg_len - done);
        memcpy(mblk, msg + done, chunk);
        for (int i = 0; i < 16; i++) T[i] ^= mblk[i];
        aes128_encrypt_block(T, rk);
        done += chunk;
    }
}

/*
 * ccm_ctr_crypt()
 * CTR mode encrypt/decrypt (symmetric).
 * Counter block A_i: flags | nonce | counter(i), big-endian 2-byte counter.
 * flags for CTR = L-1 = 0x01
 * A_0 is reserved for MAC encryption; message starts at A_1.
 */
static void ccm_ctr_crypt(const uint8_t rk[11][16],
                            const uint8_t *nonce, uint8_t nonce_len,
                            const uint8_t *input, uint8_t *output,
                            uint16_t len, uint16_t ctr_start)
{
    uint8_t  A[16];
    uint16_t ctr  = ctr_start;
    uint16_t done = 0;

    while (done < len)
    {
        /* Build counter block */
        memset(A, 0, 16);
        A[0] = 0x01;  /* flags: L-1 = 1 */
        memcpy(&A[1], nonce, nonce_len);
        A[14] = (uint8_t)(ctr >> 8);
        A[15] = (uint8_t)(ctr & 0xFF);

        /* Encrypt counter block to get keystream */
        uint8_t ks[16];
        memcpy(ks, A, 16);
        aes128_encrypt_block(ks, rk);

        /* XOR keystream with input */
        uint16_t chunk = (len - done > 16) ? 16 : (len - done);
        for (uint16_t i = 0; i < chunk; i++)
            output[done + i] = input[done + i] ^ ks[i];

        done += chunk;
        ctr++;
    }
}

/* ═══════════════════════════════════════════════════════════════════════
 * INTERNAL CCM ENCRYPT/DECRYPT
 * ═════════════════════════════════════════════════════════════════════*/

/*
 * ccm_encrypt_packet()
 * Encrypt and authenticate with the given key.
 * aad must be the UPDATED header (data_length already set for encrypted size).
 */
static bool ccm_encrypt_packet(const uint8_t *key,
                                 const uint8_t *aad,     uint8_t  aad_len,
                                 const uint8_t *nonce,   uint8_t  nonce_len,
                                 const uint8_t *plain,   uint16_t plain_len,
                                 uint8_t       *cipher,
                                 uint8_t       *mac_out)
{
    /* Static round key buffer -- keeps 176 bytes off the call stack.
     * crypto_encrypt/decrypt can be called from uart_task which has a
     * 2048-byte stack shared with dispatch and handler frames. */
    uint8_t rk[11][16];
    aes_key_expand(key, rk);

    /* Step 1: CBC-MAC over AAD + plaintext → T (16 bytes) */
    uint8_t T[16];
    ccm_cbc_mac(rk, nonce, nonce_len, aad, aad_len, plain, plain_len, T);

    /* Step 2: CTR encrypt plaintext (counter starts at 1) */
    ccm_ctr_crypt(rk, nonce, nonce_len, plain, cipher, plain_len, 1);

    /* Step 3: CTR encrypt T with counter 0 → MAC tag (take first MAC_LEN bytes) */
    uint8_t encT[16];
    ccm_ctr_crypt(rk, nonce, nonce_len, T, encT, 16, 0);
    memcpy(mac_out, encT, SCK_CRYPTO_MAC_LEN);

    return true;
}

/*
 * ccm_decrypt_packet()
 * Decrypt and verify MAC with the given key.
 * Returns true only if MAC matches.
 */
static bool ccm_decrypt_packet(const uint8_t *key,
                                 const uint8_t *aad,     uint8_t  aad_len,
                                 const uint8_t *nonce,   uint8_t  nonce_len,
                                 const uint8_t *cipher,  uint16_t cipher_len,
                                 const uint8_t *mac_in,
                                 uint8_t       *plain)
{
    /* Static round key buffer -- keeps 176 bytes off the call stack. */
    uint8_t rk[11][16];
    aes_key_expand(key, rk);

    /* Step 1: CTR decrypt ciphertext (counter starts at 1) */
    ccm_ctr_crypt(rk, nonce, nonce_len, cipher, plain, cipher_len, 1);

    /* Step 2: CBC-MAC over AAD + decrypted plaintext → T */
    uint8_t T[16];
    ccm_cbc_mac(rk, nonce, nonce_len, aad, aad_len, plain, cipher_len, T);

    /* Step 3: CTR encrypt T with counter 0 → expected MAC tag */
    uint8_t encT[16];
    ccm_ctr_crypt(rk, nonce, nonce_len, T, encT, 16, 0);

    /* Step 4: Constant-time MAC comparison */
    uint8_t diff = 0;
    for (int i = 0; i < SCK_CRYPTO_MAC_LEN; i++)
        diff |= encT[i] ^ mac_in[i];

    return (diff == 0);
}

/* ═══════════════════════════════════════════════════════════════════════
 * PUBLIC API
 * ═════════════════════════════════════════════════════════════════════*/

/* -----------------------------------------------------------------------
 * Big-endian helpers
 * --------------------------------------------------------------------- */
static void put_be32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v >> 24); p[1] = (uint8_t)(v >> 16);
    p[2] = (uint8_t)(v >>  8); p[3] = (uint8_t)(v);
}

static uint32_t get_be32(const uint8_t *p)
{
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) |
           ((uint32_t)p[2] <<  8) |  (uint32_t)p[3];
}

/* -----------------------------------------------------------------------
 * build_nonce()
 * 13-byte nonce, unique per (key, sender) for the life of the board:
 *   [0..1]   sender APID (this board)
 *   [2..5]   boot count   (boot_counter.c, +1 every boot, ext flash)
 *   [6..9]   message counter (+1 every encrypted packet this boot)
 *   [10]     SCK_CRYPTO_NONCE_VERSION
 *   [11..12] CCSDS seq word -- informational only, not used for replay
 * The whole nonce is authenticated (it is part of CCM block B_0), so a
 * forged sender, boot or counter fails the MAC.
 * Returns false once the message counter is exhausted (2^32 packets in
 * one boot -- not reachable in practice; a reboot starts a new boot count).
 * --------------------------------------------------------------------- */
static bool build_nonce(const uint8_t *ccsds_hdr, uint8_t nonce[SCK_CRYPTO_NONCE_LEN])
{
    if (sTxCounter == 0xFFFFFFFFu)
        return false;
    sTxCounter++;

    nonce[0] = (uint8_t)(sSelfApid >> 8);
    nonce[1] = (uint8_t)(sSelfApid);
    put_be32(&nonce[2], sBootCount);
    put_be32(&nonce[6], sTxCounter);
    nonce[10] = SCK_CRYPTO_NONCE_VERSION;
    nonce[11] = ccsds_hdr[2];
    nonce[12] = ccsds_hdr[3];
    return true;
}

/* -----------------------------------------------------------------------
 * Replay table helpers (decrypt task only)
 * --------------------------------------------------------------------- */
static void replay_clear(void)
{
    memset(sReplay, 0, sizeof(sReplay));
    sReplayStamp = 0;
    sStats.replayClears++;
}

static replay_entry_t *replay_find(uint16_t apid)
{
    for (int i = 0; i < SCK_CRYPTO_REPLAY_SLOTS; i++)
        if (sReplay[i].used && sReplay[i].apid == apid)
            return &sReplay[i];
    return NULL;
}

/* True if (boot, ctr) from this sender is newer than anything accepted. */
static bool replay_is_fresh(uint16_t apid, uint32_t boot, uint32_t ctr)
{
    const replay_entry_t *e = replay_find(apid);
    if (e == NULL)
        return true;
    return (boot > e->boot) || (boot == e->boot && ctr > e->ctr);
}

/* Record an authenticated packet. Evicts the least recently used entry
 * when the table is full. */
static void replay_accept(uint16_t apid, uint32_t boot, uint32_t ctr)
{
    replay_entry_t *e = replay_find(apid);
    if (e == NULL)
    {
        e = &sReplay[0];
        for (int i = 0; i < SCK_CRYPTO_REPLAY_SLOTS; i++)
        {
            if (!sReplay[i].used) { e = &sReplay[i]; break; }
            if (sReplay[i].stamp < e->stamp) e = &sReplay[i];
        }
        e->used = true;
        e->apid = apid;
    }
    e->boot  = boot;
    e->ctr   = ctr;
    e->stamp = ++sReplayStamp;
}

/* -----------------------------------------------------------------------
 * crypto_init()
 * --------------------------------------------------------------------- */
bool crypto_init(void)
{
    sCryptoReady = false;
    sTxCounter   = 0;
    memset(sReplay, 0, sizeof(sReplay));
    sClearRequest = false;
    sIdleSeconds  = 0;

    /* Self-test: verify software AES-128-CCM against known test vector.
     *
     * Test vector (confirmed by .NET AesCcm + Python cryptography):
     *   Key:       0xFF x 16
     *   Nonce:     0x00 x 11 + 0xC0 + 0x07
     *   AAD:       18 11 C0 07 00 15  (header with UPDATED data_length)
     *   Plaintext: 0x01
     *   Expected ciphertext: 0x83
     *   Expected MAC:        85 84 6C 8D 8C 2A 0B 19
     *
     * If this passes, software AES matches .NET AesCcm exactly.
     * If it fails, there is a bug in the software implementation --
     * do not operate, return false so caller can signal fault. */
    {
        static const uint8_t testKey[16] = {
            0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,
            0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF
        };
        static const uint8_t testNonce[SCK_CRYPTO_NONCE_LEN] = {
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0xC0,0x07
        };
        static const uint8_t testAAD[CRYPTO_CCSDS_HDR_LEN] = {
            0x18,0x11,0xC0,0x07,0x00,0x15
        };
        static const uint8_t testPlain[1] = { 0x01 };
        static const uint8_t expectedCipher[1]              = { 0x83 };
        static const uint8_t expectedMac[SCK_CRYPTO_MAC_LEN] = {
            0x85,0x84,0x6C,0x8D,0x8C,0x2A,0x0B,0x19
        };

        uint8_t outCipher[1]               = {0};
        uint8_t outMac[SCK_CRYPTO_MAC_LEN] = {0};

        ccm_encrypt_packet(testKey,
                           testAAD,   CRYPTO_CCSDS_HDR_LEN,
                           testNonce, SCK_CRYPTO_NONCE_LEN,
                           testPlain, 1,
                           outCipher, outMac);

        bool pass = (outCipher[0] == expectedCipher[0]);
        for (int i = 0; i < SCK_CRYPTO_MAC_LEN && pass; i++)
            if (outMac[i] != expectedMac[i]) pass = false;

        if (!pass)
            return false;  /* Software AES mismatch -- fatal */
    }

    sCryptoReady = true;
    return true;
}

/* -----------------------------------------------------------------------
 * crypto_encrypt()
 *
 * Encrypts a CCSDS packet for RF transmission using Key 0.
 *
 * Critical AAD note: the CCSDS data_length field (bytes [4:5]) in the
 * output header must reflect the ENCRYPTED packet size BEFORE the MAC
 * is computed, so the receiver uses the same AAD. This matches exactly
 * what CcsdsProtocol.cs does on the C# side.
 * --------------------------------------------------------------------- */
bool crypto_encrypt(const uint8_t *in_buf,  uint16_t  in_len,
                          uint8_t *out_buf, uint16_t *out_len)
{
    if (!sCryptoReady || in_buf == NULL || out_buf == NULL || out_len == NULL)
        return false;
    if (in_len <= CRYPTO_CCSDS_HDR_LEN)
        return false;

    uint16_t payload_len = in_len - CRYPTO_CCSDS_HDR_LEN;

    /* Build nonce (unique: sender, boot count, message counter) */
    uint8_t nonce[SCK_CRYPTO_NONCE_LEN];
    if (!build_nonce(in_buf, nonce))
        return false;

    /* Output layout: [header 6B][nonce 13B][ciphertext N bytes][MAC 8B] */
    uint16_t out_total = CRYPTO_CCSDS_HDR_LEN + SCK_CRYPTO_NONCE_LEN
                       + payload_len + SCK_CRYPTO_MAC_LEN;

    /* Build AAD = CCSDS header with UPDATED data_length (bytes [4:5]).
     * Must match C# EncryptPacket() exactly: update BEFORE MAC computation. */
    uint8_t aad[CRYPTO_CCSDS_HDR_LEN];
    memcpy(aad, in_buf, CRYPTO_CCSDS_HDR_LEN);
    uint16_t new_data_len = SCK_CRYPTO_NONCE_LEN + payload_len + SCK_CRYPTO_MAC_LEN;
    aad[4] = (uint8_t)((new_data_len - 1) >> 8);
    aad[5] = (uint8_t)((new_data_len - 1) & 0xFF);

    /* Copy updated header to output */
    memcpy(out_buf, aad, CRYPTO_CCSDS_HDR_LEN);
    /* Append nonce */
    memcpy(out_buf + CRYPTO_CCSDS_HDR_LEN, nonce, SCK_CRYPTO_NONCE_LEN);

    uint8_t *ciphertext = out_buf + CRYPTO_CCSDS_HDR_LEN + SCK_CRYPTO_NONCE_LEN;
    uint8_t *mac        = ciphertext + payload_len;

    ccm_encrypt_packet(sKeys[0],
                       aad,   CRYPTO_CCSDS_HDR_LEN,
                       nonce, SCK_CRYPTO_NONCE_LEN,
                       in_buf + CRYPTO_CCSDS_HDR_LEN, payload_len,
                       ciphertext, mac);

    *out_len = out_total;
    return true;
}

/* -----------------------------------------------------------------------
 * crypto_decrypt()
 *
 * Authenticates and decrypts a received CCSDS packet.
 * Tries all 3 keys -- accepts first valid MAC.
 * The AAD used for MAC verification is the received header bytes as-is
 * (already contain the updated data_length from the sender).
 * --------------------------------------------------------------------- */
bool crypto_decrypt(const uint8_t *in_buf,  uint16_t  in_len,
                          uint8_t *out_buf, uint16_t *out_len,
                          uint8_t *key_index)
{
    if (!sCryptoReady || in_buf == NULL || out_buf == NULL ||
        out_len == NULL || key_index == NULL)
        return false;

    if (in_len < CRYPTO_CCSDS_HDR_LEN + SCK_CRYPTO_NONCE_LEN + 1 + SCK_CRYPTO_MAC_LEN)
        return false;

    const uint8_t *nonce      = in_buf + CRYPTO_CCSDS_HDR_LEN;
    uint16_t       cipher_len = in_len - CRYPTO_CCSDS_HDR_LEN
                               - SCK_CRYPTO_NONCE_LEN - SCK_CRYPTO_MAC_LEN;
    const uint8_t *ciphertext = nonce + SCK_CRYPTO_NONCE_LEN;
    const uint8_t *mac        = ciphertext + cipher_len;

    /* Pending clear (CMD_CRYPTO_RESET_REPLAY or the silence timeout) is
     * applied here, in the only task that writes the table. */
    if (sClearRequest)
    {
        sClearRequest = false;
        replay_clear();
    }

    /* Nonce format check: packets from pre-11/2026 firmware (seq-only
     * nonce, byte 10 = 0) are refused. Both ends must run the new format. */
    if (nonce[10] != SCK_CRYPTO_NONCE_VERSION)
    {
        sStats.rejFormat++;
        return false;
    }

    /* Replay protection, per sender: (boot, counter) must move forward. */
    uint16_t sender = (uint16_t)(((uint16_t)nonce[0] << 8) | nonce[1]);
    uint32_t boot   = get_be32(&nonce[2]);
    uint32_t ctr    = get_be32(&nonce[6]);
    if (!replay_is_fresh(sender, boot, ctr))
    {
        sStats.rejReplay++;
        return false;
    }

    /* AAD = received header as-is (already has updated data_length) */
    const uint8_t *aad = in_buf;

    /* Try all 3 keys */
    for (uint8_t k = 0; k < SCK_CRYPTO_KEY_COUNT; k++)
    {
        uint8_t plaintext[SCK_CRYPTO_MAX_PAYLOAD];

        bool ok = ccm_decrypt_packet(sKeys[k],
                                     aad,   CRYPTO_CCSDS_HDR_LEN,
                                     nonce, SCK_CRYPTO_NONCE_LEN,
                                     ciphertext, cipher_len,
                                     mac,
                                     plaintext);
        if (ok)
        {
            /* Build output: [header 6B][plaintext N bytes] */
            memcpy(out_buf, in_buf, CRYPTO_CCSDS_HDR_LEN);
            memcpy(out_buf + CRYPTO_CCSDS_HDR_LEN, plaintext, cipher_len);

            /* Restore original data_length field */
            uint16_t orig = cipher_len;
            out_buf[4] = (uint8_t)((orig - 1) >> 8);
            out_buf[5] = (uint8_t)((orig - 1) & 0xFF);

            *out_len   = CRYPTO_CCSDS_HDR_LEN + cipher_len;
            *key_index = k;
            replay_accept(sender, boot, ctr);   /* only after the MAC verifies */
            sIdleSeconds = 0;
            sStats.accepted++;
            return true;
        }
    }

    sStats.rejMac++;
    return false;  /* All 3 keys failed MAC -- drop packet */
}

/* -----------------------------------------------------------------------
 * crypto_set_identity()
 * --------------------------------------------------------------------- */
void crypto_set_identity(uint16_t selfApid, uint32_t bootCount)
{
    sSelfApid  = selfApid;
    sBootCount = bootCount;
    sTxCounter = 0;
}

/* -----------------------------------------------------------------------
 * crypto_reset_replay_counter()
 * Request a replay-table clear. Safe from any task: the clear itself runs
 * at the start of the next crypto_decrypt().
 * --------------------------------------------------------------------- */
void crypto_reset_replay_counter(void)
{
    sClearRequest = true;
}

/* -----------------------------------------------------------------------
 * crypto_idle_tick()
 * Called periodically (watchdogTask, every 10 s). After
 * SCK_CRYPTO_SILENCE_RESET_S with no authenticated packet, clear the
 * replay table so a peer whose boot counter went backwards (its external
 * flash was wiped or replaced) can be heard again. Trade-off accepted
 * 11/2026: after a long silence one old recorded packet could be replayed;
 * in exchange a board can never be locked out permanently.
 * --------------------------------------------------------------------- */
void crypto_idle_tick(uint32_t seconds)
{
    uint32_t idle = sIdleSeconds + seconds;
    if (idle >= SCK_CRYPTO_SILENCE_RESET_S)
    {
        idle = 0;
        sClearRequest = true;
        sStats.silenceResets++;
    }
    sIdleSeconds = idle;
}

/* -----------------------------------------------------------------------
 * crypto_get_stats()
 * --------------------------------------------------------------------- */
void crypto_get_stats(crypto_stats_t *out)
{
    if (out == NULL) return;
    out->accepted      = sStats.accepted;
    out->rejMac        = sStats.rejMac;
    out->rejReplay     = sStats.rejReplay;
    out->rejFormat     = sStats.rejFormat;
    out->replayClears  = sStats.replayClears;
    out->silenceResets = sStats.silenceResets;
    out->bootCount     = sBootCount;
}
