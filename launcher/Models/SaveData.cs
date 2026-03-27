using System;
using System.Collections.Generic;

namespace KenshiLauncher.Models;

public class SaveData
{
    public string Name { get; set; } = "New Save";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public TimeSpan TotalSessionTime { get; set; } = TimeSpan.Zero;
    public ServerConfig Server { get; set; } = new();
    public GameStateData GameState { get; set; } = new();
    public List<PlayerRecord> Players { get; set; } = new();
}

public class ServerConfig
{
    public int Port { get; set; } = 7777;
    public int MaxPlayers { get; set; } = 8;
    public string Password { get; set; } = "";
}

public class GameStateData
{
    public float Speed { get; set; } = 1.0f;
    public Vec3 Player1Position { get; set; } = new(-5139.11f, 158.019f, 345.631f);
    public Vec3 Player2Position { get; set; } = new(-5139.11f, 158.019f, 345.631f);
}

public class Vec3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vec3() { }

    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

public class PlayerRecord
{
    public int SlotId { get; set; }
    public string Name { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string Faction { get; set; } = "";
    public string LastIP { get; set; } = "";
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public TimeSpan TotalPlaytime { get; set; } = TimeSpan.Zero;
    public bool IsOnline { get; set; }
}
