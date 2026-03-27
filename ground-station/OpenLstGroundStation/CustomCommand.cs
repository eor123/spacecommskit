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
            new CustomCommand { Name = "PICO Ping",        Opcode = 0x20, Payload = "00", Notes = "Ping the Pico — expects PICO:ACK" },
            new CustomCommand { Name = "PICO Read Temp",   Opcode = 0x20, Payload = "01", Notes = "Read onboard temperature sensor" },
            new CustomCommand { Name = "PICO Snap",        Opcode = 0x20, Payload = "02", Notes = "Trigger camera snapshot" },
            new CustomCommand { Name = "PICO List Files",  Opcode = 0x20, Payload = "03", Notes = "List files on SD card" },
            new CustomCommand { Name = "PICO Get File",    Opcode = 0x20, Payload = "04", Notes = "Download file from SD card" },
            new CustomCommand { Name = "PICO Delete File", Opcode = 0x20, Payload = "05", Notes = "Delete file from SD card" },
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
