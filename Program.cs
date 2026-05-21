using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.NetworkInformation;

string multicastGroup;
int multicastPort;
int mode;
bool quit = false;

Console.Title = "MulticastApp";
Console.Clear();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("=== MulticastApp ===");
Console.ResetColor();
Console.WriteLine();

// List all available relevant IP addresses
List<IPAddress> localIPs = new List<IPAddress>();
int ipIndex = 1;

Console.WriteLine("Available network interfaces:");
foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces()) {
    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses) {
        if (ip.Address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(ip.Address) &&
            !ip.Address.ToString().StartsWith("169.254")) {
            localIPs.Add(ip.Address);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  {ipIndex++}. ");
            Console.ResetColor();
            Console.WriteLine($"{ip.Address}  ({ni.Name})");
        }
    }
}

Console.WriteLine();
IPAddress localIP = null!;
while (true) {
    Console.Write("Select interface number: ");
    if (int.TryParse(Console.ReadLine(), out int ipChoice) && ipChoice > 0 && ipChoice <= localIPs.Count) {
        localIP = localIPs[ipChoice - 1];
        break;
    }
    PrintError("Invalid choice. Please select a valid number.");
}

while (true) {
    Console.Write("Multicast group (224.0.0.0 - 239.255.255.255): ");
    multicastGroup = Console.ReadLine()!;

    if (IPAddress.TryParse(multicastGroup, out IPAddress? addr)) {
        byte[] bytes = addr.GetAddressBytes();
        if (bytes[0] >= 224 && bytes[0] <= 239) {
            break;
        }
    }

    PrintError("Invalid multicast address. Must be in range 224.0.0.0 - 239.255.255.255.");
}

while (true) {
    Console.Write("Port: ");
    if (int.TryParse(Console.ReadLine(), out multicastPort) && multicastPort > 0 && multicastPort <= 65535) {
        break;
    }

    PrintError("Invalid port. Enter a number between 1 and 65535.");
}

while (true) {
    Console.Write("Mode (1 = Sender, 2 = Receiver): ");
    if (int.TryParse(Console.ReadLine(), out mode) && (mode == 1 || mode == 2)) {
        break;
    }

    PrintError("Invalid choice. Enter 1 or 2.");
}

// For sender, collect message before switching to fixed layout
string senderMessage = string.Empty;
if (mode == 1) {
    Console.Write("Message to send: ");
    senderMessage = Console.ReadLine()!;
}

// Switch to fixed layout
string modeLabel = mode == 1 ? "SENDER" : "RECEIVER";
UI.Init($"=== {modeLabel} | {multicastGroup}:{multicastPort} via {localIP} ===");

Thread keyListener = new Thread(() => {
    while (!quit) {
        if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) {
            quit = true;
        }
    }
});
keyListener.IsBackground = true;
keyListener.Start();

if (mode == 1) {
    StartSender(localIP, multicastGroup, multicastPort, senderMessage, ref quit);
} else {
    StartReceiver(localIP, multicastGroup, multicastPort, ref quit);
}

Console.CursorVisible = true;
Console.SetCursorPosition(0, Console.WindowHeight - 1);
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("Stopped.");
Console.ResetColor();

static void StartSender(IPAddress localIP, string multicastGroup, int multicastPort, string message, ref bool quit) {
    using var udpClient = new UdpClient(new IPEndPoint(localIP, 0));

    // Force multicast traffic out through the selected interface
    udpClient.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
        localIP.GetAddressBytes());

    IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Parse(multicastGroup), multicastPort);
    byte[] data = Encoding.UTF8.GetBytes(message);

    UI.Log($"Message : \"{message}\"", ConsoleColor.White);

    int sentCount = 0;
    while (!quit) {
        udpClient.Send(data, data.Length, remoteEndPoint);
        sentCount++;
        UI.UpdateCounter($"Sent: {sentCount,-6}  Last: {DateTime.Now:HH:mm:ss}");
        UI.Log($"[{DateTime.Now:HH:mm:ss}] Sent to {multicastGroup}:{multicastPort}  \"{message}\"", ConsoleColor.Cyan);
        Thread.Sleep(1000);
    }
}

static void StartReceiver(IPAddress localIP, string multicastGroup, int multicastPort, ref bool quit) {
    // Create socket first so ReuseAddress can be set before Bind
    using var udpClient = new UdpClient();
    udpClient.ExclusiveAddressUse = false;
    udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

    // Bind to ANY — multicast packets are addressed to the group IP, not localIP
    udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, multicastPort));

    // Join the group on the specific interface the user selected
    udpClient.JoinMulticastGroup(IPAddress.Parse(multicastGroup), localIP);

    // Receive timeout lets the loop check the quit flag without busy-waiting
    udpClient.Client.ReceiveTimeout = 500;

    int receivedCount = 0;
    while (!quit) {
        try {
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
            byte[] data = udpClient.Receive(ref remoteEndPoint);
            receivedCount++;
            UI.UpdateCounter($"Received: {receivedCount,-6}  Last: {DateTime.Now:HH:mm:ss}");

            try {
                string msg = Encoding.UTF8.GetString(data);
                UI.Log($"[{DateTime.Now:HH:mm:ss}] {remoteEndPoint.Address}:{remoteEndPoint.Port}  \"{msg}\"", ConsoleColor.Green);
            } catch (DecoderFallbackException) {
                UI.Log($"[{DateTime.Now:HH:mm:ss}] {remoteEndPoint.Address}:{remoteEndPoint.Port}  (non-UTF-8, {data.Length} bytes)", ConsoleColor.DarkYellow);
            }
        } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) {
            // Normal timeout — loop back to check quit flag
        }
    }
}

static void PrintError(string message) {
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(message);
    Console.ResetColor();
}

// Fixed-layout console UI: header pinned at top, log scrolls below
static class UI {
    const int HeaderRows = 5; // title, hint, separator, counter, separator

    static readonly List<(string Text, ConsoleColor Color)> _log = new();
    static string _header = string.Empty;
    static string _counter = string.Empty;
    static readonly object _lock = new();

    public static void Init(string header) {
        _header = header;
        Console.Clear();
        Console.CursorVisible = false;
        Redraw();
    }

    public static void UpdateCounter(string text) {
        lock (_lock) {
            _counter = text;
            int saved = Console.CursorTop;
            Console.SetCursorPosition(0, 3);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  {_counter}".PadRight(Console.WindowWidth - 1));
            Console.ResetColor();
            Console.SetCursorPosition(0, saved);
        }
    }

    public static void Log(string text, ConsoleColor color = ConsoleColor.White) {
        lock (_lock) {
            _log.Add((text, color));
            RedrawLog();
        }
    }

    static void Redraw() {
        Console.SetCursorPosition(0, 0);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write(_header.PadRight(Console.WindowWidth - 1));
        Console.ResetColor();
        Console.WriteLine();
        Console.Write("Press Esc to quit.".PadRight(Console.WindowWidth - 1));
        Console.WriteLine();
        Console.Write(new string('-', Console.WindowWidth - 1));
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"  {_counter}".PadRight(Console.WindowWidth - 1));
        Console.ResetColor();
        Console.WriteLine();
        Console.Write(new string('-', Console.WindowWidth - 1));
        RedrawLog();
    }

    static void RedrawLog() {
        int contentRows = Console.WindowHeight - HeaderRows - 1;
        var visible = _log.TakeLast(contentRows).ToList();

        for (int i = 0; i < contentRows; i++) {
            Console.SetCursorPosition(0, HeaderRows + i);
            if (i < visible.Count) {
                Console.ForegroundColor = visible[i].Color;
                string line = "  " + visible[i].Text;
                Console.Write(line.PadRight(Console.WindowWidth - 1));
                Console.ResetColor();
            } else {
                Console.Write(new string(' ', Console.WindowWidth - 1));
            }
        }
    }
}
