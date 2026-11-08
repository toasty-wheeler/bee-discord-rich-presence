// BeePresence: a minimal MusicBee -> Discord "Listening to" presence plugin.
// No third-party libraries. It talks to the Discord desktop client over its local
// IPC named pipe and gets cover art URLs from the public iTunes Search API, so
// nothing is uploaded anywhere.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MusicBeePlugin
{
  public partial class Plugin
  {
    private MusicBeeApiInterface mbApi;
    private readonly PluginInfo about = new PluginInfo();

    private string clientId = "910864681588105236"; // DiscordBee's app ("MusicBee"); override in BeePresence.ini
    private bool showWhenPaused = false;
    private bool coverArt = true;
    private string logPath, iniPath;

    private Thread worker;
    private volatile bool running;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private Track desired;              // what Discord should show (null = nothing)
    private readonly object stateLock = new object();

    private DiscordIpc ipc;
    private string lastSentJson;
    private DateTime lastSendUtc = DateTime.MinValue;

    private static readonly HttpClient http = CreateHttp();
    private readonly ConcurrentDictionary<string, string> artCache = new ConcurrentDictionary<string, string>();

    private sealed class Track
    {
      public string Title, Artist, Album;
      public bool Paused;
      public long StartMs, EndMs; // unix ms, 0 = unknown
    }

    public PluginInfo Initialise(IntPtr apiInterfacePtr)
    {
      mbApi = new MusicBeeApiInterface();
      mbApi.Initialise(apiInterfacePtr);
      about.PluginInfoVersion = PluginInfoVersion;
      about.Name = "BeePresence";
      about.Description = "Shows what you're listening to on Discord (lightweight DiscordBee replacement)";
      about.Author = "Toasty";
      about.TargetApplication = "";
      about.Type = PluginType.General;
      about.VersionMajor = 1;
      about.VersionMinor = 0;
      about.Revision = 0;
      about.MinInterfaceVersion = MinInterfaceVersion;
      about.MinApiRevision = MinApiRevision;
      about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
      about.ConfigurationPanelHeight = 0;

      string dir = mbApi.Setting_GetPersistentStoragePath();
      iniPath = Path.Combine(dir, "BeePresence.ini");
      logPath = Path.Combine(dir, "BeePresence.log");
      LoadSettings();

      running = true;
      worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BeePresence" };
      worker.Start();
      return about;
    }

    public bool Configure(IntPtr panelHandle)
    {
      // No UI: open the ini in the default editor. Changes apply after restarting MusicBee
      // (or disabling/enabling the plugin).
      try { LoadSettings(); System.Diagnostics.Process.Start(iniPath); } catch { }
      return false;
    }

    public void SaveSettings() { }

    public void Close(PluginCloseReason reason)
    {
      running = false;
      wake.Set();
      try { worker?.Join(1500); } catch { }
      try { ipc?.Dispose(); } catch { }
    }

    public void Uninstall()
    {
      try { File.Delete(iniPath); File.Delete(logPath); } catch { }
    }

    public void ReceiveNotification(string sourceFileUrl, NotificationType type)
    {
      switch (type)
      {
        case NotificationType.PluginStartup:
        case NotificationType.TrackChanged:
        case NotificationType.PlayStateChanged:
          Refresh();
          break;
      }
    }

    // Snapshot MusicBee's player state into `desired` and poke the worker.
    private void Refresh()
    {
      Track t = null;
      try
      {
        var state = mbApi.Player_GetPlayState();
        bool playing = state == PlayState.Playing;
        bool paused = state == PlayState.Paused;
        if ((playing || (paused && showWhenPaused)) && !string.IsNullOrEmpty(mbApi.NowPlaying_GetFileUrl()))
        {
          t = new Track
          {
            Title = Tag(MetaDataType.TrackTitle),
            Artist = Tag(MetaDataType.Artist),
            Album = Tag(MetaDataType.Album),
            Paused = paused,
          };
          if (string.IsNullOrEmpty(t.Title))
            t.Title = Path.GetFileNameWithoutExtension(mbApi.NowPlaying_GetFileUrl());
          if (playing)
          {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int pos = mbApi.Player_GetPosition();
            int dur = mbApi.NowPlaying_GetDuration();
            t.StartMs = now - Math.Max(0, pos);
            if (dur > 0) t.EndMs = t.StartMs + dur;
          }
        }
      }
      catch (Exception e) { Log("refresh: " + e.Message); }

      lock (stateLock) desired = t;
      wake.Set();
    }

    private string Tag(MetaDataType f) => (mbApi.NowPlaying_GetFileTag(f) ?? "").Trim();

    private void WorkerLoop()
    {
      while (running)
      {
        wake.WaitOne(5000);
        if (!running) break;
        try
        {
          // Coalesce bursts (fast skipping) and stay under Discord's 5-updates/20s limit.
          Thread.Sleep(300);
          var sinceLast = DateTime.UtcNow - lastSendUtc;
          if (sinceLast < TimeSpan.FromSeconds(4)) Thread.Sleep(TimeSpan.FromSeconds(4) - sinceLast);

          DetectSeek();

          Track t;
          lock (stateLock) t = desired;
          string json = BuildActivityJson(t);

          if (ipc == null || !ipc.Connected)
          {
            ipc?.Dispose();
            ipc = DiscordIpc.TryConnect(clientId, Log);
            lastSentJson = null;
            if (ipc == null) continue; // Discord not running; retry in 5s
          }
          if (json == lastSentJson) continue;

          ipc.SetActivity(json);
          lastSentJson = json;
          lastSendUtc = DateTime.UtcNow;
        }
        catch (Exception e)
        {
          Log("worker: " + e.Message);
          try { ipc?.Dispose(); } catch { }
          ipc = null;
        }
      }
      // Leave a clean status behind when MusicBee closes.
      try { ipc?.SetActivity("null"); } catch { }
    }

    // MusicBee doesn't raise an event on seek, so re-snapshot if the position has drifted.
    private void DetectSeek()
    {
      Track t;
      lock (stateLock) t = desired;
      if (t == null || t.Paused || t.StartMs == 0) return;
      try
      {
        if (mbApi.Player_GetPlayState() != PlayState.Playing) return;
        long expectedStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - mbApi.Player_GetPosition();
        if (Math.Abs(expectedStart - t.StartMs) > 2500) Refresh();
      }
      catch { }
    }

    private string BuildActivityJson(Track t)
    {
      if (t == null) return "null";
      var sb = new StringBuilder("{\"type\":2,\"status_display_type\":1");
      sb.Append(",\"details\":").Append(J(Field(t.Title)));
      sb.Append(",\"state\":").Append(J(Field(string.IsNullOrEmpty(t.Artist) ? "Unknown artist" : t.Artist) + (t.Paused ? " (paused)" : "")));
      if (t.StartMs > 0)
      {
        sb.Append(",\"timestamps\":{\"start\":").Append(t.StartMs);
        if (t.EndMs > 0) sb.Append(",\"end\":").Append(t.EndMs);
        sb.Append('}');
      }
      string art = coverArt ? GetCoverUrl(t) : null;
      if (art != null)
      {
        sb.Append(",\"assets\":{\"large_image\":").Append(J(art));
        if (!string.IsNullOrEmpty(t.Album)) sb.Append(",\"large_text\":").Append(J(Field(t.Album)));
        sb.Append('}');
      }
      sb.Append('}');
      return sb.ToString();
    }

    // Discord requires 2..128 chars for text fields (single-char titles are rejected).
    private static string Field(string s)
    {
      s = s ?? "";
      if (s.Length < 2) s = s.PadRight(2, '⠀');
      if (s.Length > 128) s = s.Substring(0, 127) + "…";
      return s;
    }

    // Look up album art on the iTunes Search API (free, no key). Discord accepts
    // external https URLs as large_image directly, so nothing needs to be uploaded.
    private string GetCoverUrl(Track t)
    {
      string key = (t.Artist + "|" + (string.IsNullOrEmpty(t.Album) ? "#" + t.Title : t.Album)).ToLowerInvariant();
      if (artCache.TryGetValue(key, out var cached)) return cached;
      string url = null;
      try
      {
        bool byAlbum = !string.IsNullOrEmpty(t.Album);
        string term = t.Artist + " " + (byAlbum ? t.Album : t.Title);
        string q = "https://itunes.apple.com/search?media=music&limit=1&entity=" + (byAlbum ? "album" : "song") +
                   "&term=" + Uri.EscapeDataString(term);
        string body = http.GetStringAsync(q).GetAwaiter().GetResult();
        var m = Regex.Match(body, "\"artworkUrl100\"\\s*:\\s*\"(https?://[^\"]+)\"");
        if (m.Success) url = m.Groups[1].Value.Replace("100x100bb", "512x512bb");
        else if (byAlbum)
        {
          // Album titles in local tags are often messy; retry with the track title.
          q = "https://itunes.apple.com/search?media=music&limit=1&entity=song&term=" + Uri.EscapeDataString(t.Artist + " " + t.Title);
          body = http.GetStringAsync(q).GetAwaiter().GetResult();
          m = Regex.Match(body, "\"artworkUrl100\"\\s*:\\s*\"(https?://[^\"]+)\"");
          if (m.Success) url = m.Groups[1].Value.Replace("100x100bb", "512x512bb");
        }
        artCache[key] = url; // cache misses too, so we don't hammer the API
      }
      catch (Exception e) { Log("art: " + e.Message); } // network error: don't cache, retry next time
      return url;
    }

    private static string J(string s)
    {
      var sb = new StringBuilder("\"");
      foreach (char c in s)
      {
        switch (c)
        {
          case '"': sb.Append("\\\""); break;
          case '\\': sb.Append("\\\\"); break;
          case '\n': sb.Append("\\n"); break;
          case '\r': sb.Append("\\r"); break;
          case '\t': sb.Append("\\t"); break;
          default:
            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
            break;
        }
      }
      return sb.Append('"').ToString();
    }

    private static HttpClient CreateHttp()
    {
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      var h = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
      h.DefaultRequestHeaders.UserAgent.ParseAdd("BeePresence/1.0 (MusicBee plugin)");
      return h;
    }

    private void LoadSettings()
    {
      try
      {
        if (!File.Exists(iniPath))
        {
          File.WriteAllText(iniPath,
            "; BeePresence settings. Restart MusicBee after editing.\r\n" +
            "; client_id = a Discord application ID (https://discord.com/developers/applications).\r\n" +
            ";   The application's name is what shows as \"Listening to <name>\" in the member list.\r\n" +
            "client_id=" + clientId + "\r\n" +
            "show_when_paused=false\r\n" +
            "cover_art=true\r\n");
        }
        foreach (var raw in File.ReadAllLines(iniPath))
        {
          var line = raw.Trim();
          if (line.StartsWith(";") || !line.Contains("=")) continue;
          var k = line.Substring(0, line.IndexOf('=')).Trim().ToLowerInvariant();
          var v = line.Substring(line.IndexOf('=') + 1).Trim();
          if (k == "client_id" && v.Length > 0) clientId = v;
          else if (k == "show_when_paused") showWhenPaused = v.Equals("true", StringComparison.OrdinalIgnoreCase);
          else if (k == "cover_art") coverArt = !v.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
      }
      catch (Exception e) { Log("settings: " + e.Message); }
    }

    private readonly object logLock = new object();
    private void Log(string msg)
    {
      try
      {
        lock (logLock)
        {
          var fi = new FileInfo(logPath);
          if (fi.Exists && fi.Length > 256 * 1024) fi.Delete(); // never let it grow like DiscordBee's did
          File.AppendAllText(logPath, DateTime.Now.ToString("s") + " " + msg + "\r\n");
        }
      }
      catch { }
    }
  }

  /// Minimal Discord RPC client over the local named pipe (\\.\pipe\discord-ipc-N).
  /// Frame = int32 opcode + int32 length (little endian) + UTF-8 JSON.
  internal sealed class DiscordIpc : IDisposable
  {
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private readonly NamedPipeClientStream pipe;
    private readonly Action<string> log;
    private readonly object writeLock = new object();
    private volatile bool connected = true;
    public bool Connected => connected && pipe.IsConnected;

    private DiscordIpc(NamedPipeClientStream p, Action<string> log) { pipe = p; this.log = log; }

    public static DiscordIpc TryConnect(string clientId, Action<string> log)
    {
      for (int i = 0; i < 10; i++)
      {
        var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
        // ^ Asynchronous (overlapped) is required: on a synchronous handle Windows serializes I/O,
        //   so the blocking read in ReadLoop would stall every write.
        try { p.Connect(150); }
        catch { p.Dispose(); continue; }

        var c = new DiscordIpc(p, log);
        try
        {
          c.Write(OpHandshake, "{\"v\":1,\"client_id\":\"" + clientId + "\"}");
          var (op, body) = c.Read();
          if (op == OpFrame && body.Contains("\"READY\""))
          {
            new Thread(c.ReadLoop) { IsBackground = true, Name = "BeePresence-ipc" }.Start();
            log("connected to discord-ipc-" + i);
            return c;
          }
          log("handshake rejected on discord-ipc-" + i + ": " + body);
        }
        catch (Exception e) { log("handshake failed: " + e.Message); }
        c.Dispose();
      }
      return null;
    }

    public void SetActivity(string activityJson)
    {
      string payload = "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" +
        System.Diagnostics.Process.GetCurrentProcess().Id + ",\"activity\":" + activityJson +
        "},\"nonce\":\"" + Guid.NewGuid().ToString("N") + "\"}";
      Write(OpFrame, payload);
    }

    // Drain responses so the pipe never fills, answer pings, and surface errors.
    private void ReadLoop()
    {
      try
      {
        while (connected)
        {
          var (op, body) = Read();
          if (op == OpPing) Write(OpPong, body);
          else if (op == OpClose) { log("discord closed pipe: " + body); break; }
          else if (body.Contains("\"evt\":\"ERROR\"")) log("discord error: " + body);
        }
      }
      catch (Exception) { }
      connected = false;
    }

    private void Write(int op, string json)
    {
      byte[] data = Encoding.UTF8.GetBytes(json);
      byte[] frame = new byte[8 + data.Length];
      BitConverter.GetBytes(op).CopyTo(frame, 0);
      BitConverter.GetBytes(data.Length).CopyTo(frame, 4);
      data.CopyTo(frame, 8);
      lock (writeLock) { pipe.Write(frame, 0, frame.Length); pipe.Flush(); }
    }

    private (int op, string body) Read()
    {
      byte[] hdr = ReadExact(8);
      int op = BitConverter.ToInt32(hdr, 0), len = BitConverter.ToInt32(hdr, 4);
      if (len < 0 || len > 1 << 20) throw new IOException("bad frame length");
      return (op, Encoding.UTF8.GetString(ReadExact(len)));
    }

    private byte[] ReadExact(int n)
    {
      var buf = new byte[n];
      int got = 0;
      while (got < n)
      {
        int r = pipe.Read(buf, got, n - got);
        if (r <= 0) throw new EndOfStreamException();
        got += r;
      }
      return buf;
    }

    public void Dispose() { connected = false; try { pipe.Dispose(); } catch { } }
  }
}
