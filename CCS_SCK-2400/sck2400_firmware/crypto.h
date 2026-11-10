/*
 * crypto.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   AES-128 CCM per-packet encryption and authentication.
 *   Pure software implementation (RFC 3610 / NIST SP 800-38C).
 *   No TI hardware accelerator dependency.
 *
 * Security model:
 *   Every CCSDS packet transmitted over RF is encrypted and authenticated
 *   using AES-128 CCM with an 8-byte MAC tag. The 13-byte nonce is
 *   [sender APID 2B][boot count 4B][message counter 4B][version 1B]
 *   [CCSDS seq 2B]. It never repeats for a key (boot count is kept in
 *   external flash, boot_counter.c), and each receiver keeps the highest
 *   (boot, counter) per sender to reject replays. Changed 11/2026 --
 *   the old seq-only nonce locked boards out when the ground app
 *   restarted and wrapped every 16384 packets.
 *
 *   Three keys are accepted for incoming packets (Option A -- 3-key fallback).
 *   Outgoing packets are encrypted with Key 0.
 *
 * Packet wire format on air:
 *   [CCSDS Header 6B][Nonce 13B][Encrypted Payload N bytes][MAC 8B]
 *
 *   The CCSDS header stays plaintext -- it is needed for routing.
 *   The nonce is transmitted plaintext -- it is not secret, only unique.
 *   Everything after the nonce is encrypted and authenticated.
 *
 * CCM parameters:
 *   Key length:   16 bytes (AES-128)
 *   Nonce length: 13 bytes (L=2, N=13, L+N=15 per RFC 3610)
 *   MAC length:    8 bytes
 *   L (length field size): 2 bytes (supports msg up to 65535 bytes)
 *
 * Verified test vector:
 *   Key=0xFF*16, Nonce=0x00*11+0xC0+0x07, AAD=18 11 C0 07 00 15,
 *   Plaintext=0x01 → Ciphertext=0x83, MAC=85 84 6C 8D 8C 2A 0B 19
 *   Confirmed against .NET AesCcm and Python cryptography library.
 *
 * Architecture:
 *   crypto_init() runs a self-test against the known vector.
 *   Returns false (fatal) if the test fails -- do not operate without
 *   verified crypto.
 *
 *   crypto_encrypt() and crypto_decrypt() are safe to call from any
 *   RTOS task context -- no semaphores, no hardware dependencies.
 *
 * Target:    CC1352P1F3RGZ / CC2652P1FRGZ
 * Toolchain: TI Clang + TI-RTOS7
 * Reference: RFC 3610, NIST SP 800-38C, FIPS 197
 */

#ifndef CRYPTO_H
#define CRYPTO_H

#include <stdint.h>
#include <stdbool.h>

/* -----------------------------------------------------------------------
 * Crypto Parameters
 * Must match CcsdsProtocol.cs CRYPTO_* constants on the C# side.
 * --------------------------------------------------------------------- */
#define SCK_CRYPTO_KEY_LEN      16  /* AES-128: 16 bytes                  */
#define SCK_CRYPTO_KEY_COUNT     3  /* 3-key fallback (Option A)           */
#define SCK_CRYPTO_MAC_LEN       8  /* CCM MAC tag: 8 bytes               */
#define SCK_CRYPTO_NONCE_LEN    13  /* 13-byte nonce (L=2, N=13, L+N=15)  */

/* Nonce format version (byte 10). Packets with any other value -- e.g.
 * from firmware before 11/2026 -- are rejected. */
#define SCK_CRYPTO_NONCE_VERSION    0x01

/* Senders tracked by the replay table (GS + remotes). */
#define SCK_CRYPTO_REPLAY_SLOTS     4

/* Replay table is cleared after this long with no authenticated packet,
 * so a board can never stay locked out (see crypto_idle_tick()). */
#ifndef SCK_CRYPTO_SILENCE_RESET_S
#define SCK_CRYPTO_SILENCE_RESET_S  (6u * 3600u)   /* 6 hours */
#endif

/* Per-packet overhead on air: nonce + MAC */
#define SCK_CRYPTO_OVERHEAD     (SCK_CRYPTO_NONCE_LEN + SCK_CRYPTO_MAC_LEN)

/* Maximum plaintext payload (limited by RF buffer minus header and overhead) */
#define SCK_CRYPTO_MAX_PAYLOAD  (255 - 6 - SCK_CRYPTO_OVERHEAD)

/* -----------------------------------------------------------------------
 * Public API
 * --------------------------------------------------------------------- */

/*
 * crypto_init()
 *
 * Initialize the software AES-128-CCM engine and run self-test.
 *
 * The self-test encrypts a known plaintext with the known test vector
 * and verifies the output matches the .NET AesCcm / Python reference.
 * If the test fails, this function returns false -- do not operate.
 *
 * Safe to call from any task context. No hardware dependencies.
 *
 * Returns: true on success (self-test passed),
 *          false if self-test failed (software AES bug -- fatal).
 */
bool crypto_init(void);

/*
 * crypto_encrypt()
 *
 * Encrypt and authenticate a CCSDS packet for RF transmission.
 * Uses Key 0. Safe to call from any task context.
 *
 * Input:  [CCSDS header 6B][plaintext payload N bytes]
 * Output: [CCSDS header 6B][nonce 13B][ciphertext N bytes][MAC 8B]
 *
 * The CCSDS header bytes [4:5] (data_length) in the output are updated
 * to reflect the encrypted packet size BEFORE MAC computation, matching
 * exactly what CcsdsProtocol.cs does on the C# side.
 *
 * Parameters:
 *   in_buf   -- input: full CCSDS packet (header + plaintext)
 *   in_len   -- input length
 *   out_buf  -- output: encrypted packet (must be >= in_len + SCK_CRYPTO_OVERHEAD)
 *   out_len  -- actual output bytes written
 *
 * Returns: true on success, false if not initialized or invalid args.
 */
bool crypto_encrypt(const uint8_t *in_buf,  uint16_t  in_len,
                          uint8_t *out_buf, uint16_t *out_len);

/*
 * crypto_decrypt()
 *
 * Authenticate and decrypt a received CCSDS packet.
 * Tries all 3 keys -- accepts first valid MAC (Option A 3-key fallback).
 * Safe to call from any task context.
 *
 * Input:  [CCSDS header 6B][nonce 13B][ciphertext N bytes][MAC 8B]
 * Output: [CCSDS header 6B][plaintext payload N bytes]
 *
 * Replay protection: per sender APID, rejects packets whose
 * (boot count, message counter) does not advance beyond the last accepted
 * value from that sender. Also rejects pre-11/2026 nonce formats.
 *
 * Parameters:
 *   in_buf    -- input: received encrypted packet
 *   in_len    -- input length
 *   out_buf   -- output: decrypted packet
 *   out_len   -- actual output bytes written
 *   key_index -- which key authenticated the packet (0-2, output)
 *
 * Returns: true if MAC verified and decrypted,
 *          false if all 3 keys failed or replay detected.
 *          Silently drop packets that return false.
 */
bool crypto_decrypt(const uint8_t *in_buf,  uint16_t  in_len,
                          uint8_t *out_buf, uint16_t *out_len,
                          uint8_t *key_index);

/*
 * crypto_set_identity()
 * Set this board's nonce identity. Call once per boot after crypto_init()
 * and before the first crypto_encrypt():
 *   selfApid  -- SCK_APID_THIS_BOARD
 *   bootCount -- boot_counter_next()
 */
void crypto_set_identity(uint16_t selfApid, uint32_t bootCount);

/*
 * crypto_reset_replay_counter()
 * Request a clear of the replay table (all senders). Safe from any task;
 * takes effect at the next crypto_decrypt(). Used by
 * CMD_CRYPTO_RESET_REPLAY (local UART only) and the silence timeout.
 */
void crypto_reset_replay_counter(void);

/*
 * crypto_idle_tick()
 * Call periodically with the seconds elapsed (watchdogTask: every 10 s).
 * Clears the replay table after SCK_CRYPTO_SILENCE_RESET_S without an
 * authenticated packet.
 */
void crypto_idle_tick(uint32_t seconds);

/* Counters for diagnostics (all since boot). */
typedef struct
{
    uint32_t accepted;       /* packets authenticated and decrypted      */
    uint32_t rejMac;         /* MAC failed with all 3 keys               */
    uint32_t rejReplay;      /* (boot, counter) not newer than last seen */
    uint32_t rejFormat;      /* wrong nonce version (old firmware)       */
    uint32_t replayClears;   /* replay table clears (command + silence)  */
    uint32_t silenceResets;  /* clears triggered by the silence timeout  */
    uint32_t bootCount;      /* this board's boot count (0 = ext flash failed) */
} crypto_stats_t;

void crypto_get_stats(crypto_stats_t *out);

#endif /* CRYPTO_H */
