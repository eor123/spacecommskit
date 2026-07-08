// CustomCommand.cs
// Model for user-defined commands saved to customcommands.json

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OpenLstGroundStation
{
    public class CustomCommand
    {
        public string Name    { get; set; } = "";
        public byte   Opcode  { get; set; } = 0x00;
        public string Type    { get; set; } = "RawHex"; // "RawHex" or "Structured"
        public string Payload { get; set; } = "";        // hex string for RawHex
        public string Notes   { get; set; } = "";
    }

    public static class CustomCommandStore
    {
        private static string FilePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "customcommands.json");

        public static List<CustomCommand> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return Defaults();
                string json = File.ReadAllText(FilePath);
                var list = JsonSerializer.Deserialize<List<CustomCommand>>(json)
                           ?? new List<CustomCommand>();
                return list.Count == 0 ? Defaults() : list;
            }
            catch { return Defaults(); }
        }

        // ── Default Pico command set ───────────────────────────────────────
        // Pre-populated on first run. Users can add/edit/delete from the UI.
        public static List<CustomCommand> Defaults() => new List<CustomCommand>
        {
            // SCK-2400: each command has its own CCSDS sub-opcode (0x20-0x29)
            // The CC1352P bridges CCSDS → ESP framing → Pico main.py
            new CustomCommand { Name = "PICO Ping",        Opcode = 0x20, Payload = "", Notes = "Ping the Pico — expects PICO:ACK" },
            new CustomCommand { Name = "PICO Read Temp",   Opcode = 0x21, Payload = "", Notes = "Read onboard temperature sensor — expects TEMP:xx.xxC" },
            new CustomCommand { Name = "PICO Snap",        Opcode = 0x22, Payload = "", Notes = "Trigger camera snapshot — expects SNAP:OK:filename:bytes" },
            new CustomCommand { Name = "PICO List Files",  Opcode = 0x23, Payload = "", Notes = "List files on SD card — expects LIST:..." },
            new CustomCommand { Name = "PICO Get GPS",     Opcode = 0x27, Payload = "", Notes = "Get GPS + baro data — expects GPS:lat,lon,alt,sats,fix,hpa,balt,temp" },
            new CustomCommand { Name = "PICO Get Baro",    Opcode = 0x28, Payload = "", Notes = "Get barometric data — expects BARO:hpa,alt,temp" },
            new CustomCommand { Name = "PICO Beacon ON",   Opcode = 0x29, Payload = "01", Notes = "Enable autonomous GPS beacon" },
            new CustomCommand { Name = "PICO Beacon OFF",  Opcode = 0x29, Payload = "00", Notes = "Disable autonomous GPS beacon" },
        };

        public static void Save(List<CustomCommand> commands)
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(FilePath, JsonSerializer.Serialize(commands, opts));
            }
            catch { }
        }
    }
}
