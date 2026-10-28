# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A kid-friendly WPF control panel (Windows only, .NET 10, `net10.0-windows`) that sends commands to a local Minecraft Bedrock (Minecraft for Windows) game. Bedrock has no RCON, so the app hosts a WebSocket server that Minecraft connects to via the in-game `/connect 127.0.0.1:<port>` chat command.

## Commands

```
dotnet build
dotnet run --project MinecraftLittleAdmin
```

There are no tests and no lint configuration. The solution file is `MinecraftLittleAdmin.slnx` (new XML solution format) with a single project.

## Architecture

Three pieces, all in `MinecraftLittleAdmin/`:

- **`MinecraftBridge`** — a hand-rolled WebSocket server. It uses a raw `TcpListener` on `IPAddress.Loopback` and performs the HTTP upgrade handshake itself (`Sec-WebSocket-Accept` via SHA1), then wraps the stream with `WebSocket.CreateFromStream`. This avoids `HttpListener`, which needs URL ACLs/admin rights. Only one Minecraft connection is kept: a new connection replaces (aborts) the previous one. Commands are sent as Bedrock `commandRequest` JSON messages with a GUID `requestId`; replies are matched back through a `_pending` dictionary of `TaskCompletionSource`s, with a 5s timeout. Success is `statusCode >= 0`. `ConnectionChanged` fires on a background thread, so the UI marshals it with `Dispatcher.Invoke`.
- **`MinecraftWindow`** — Win32 P/Invoke automation for the "Connect to Minecraft" button. Finds the window by title (`Minecraft` / `Minecraft Preview`), focuses it, presses `T` to open chat, pastes the command from the clipboard with Ctrl+V (paste instead of typing so it works with any keyboard layout), and presses Enter. The `InputUnion` must include `MOUSEINPUT` so `INPUT` has the size `SendInput` expects.
- **`MainWindow`** — starts the bridge on port 3000, falling back through the next 9 ports if busy. On Connect, it tries the chat automation once, and if no connection arrives within 4s, retries with Esc first (Minecraft may show its pause menu on refocus).

### Adding a command button

World buttons are data-driven: each `Button` in `MainWindow.xaml` carries the Minecraft command (no leading slash) in its `Tag` and shares the `WorldButton_Click` handler. To add one:

1. Add the `Button` in `MainWindow.xaml` with `Style="{StaticResource BigButton}"`, a `Tag`, and `Click="WorldButton_Click"`.
2. Add a kid-friendly message for that exact command string to `FriendlyMessages` in `MainWindow.xaml.cs`.
3. Add the button to the array in `SetButtonsEnabled` so it is disabled while disconnected.
4. Update the command table in `README.md`.

Gamemode commands target `@s` because commands run with the host player as origin, so they only affect the host. Time and weather affect the whole LAN world.

Shared styles (`BigButton`, `SmallButton`, `ButtonLabel`), the `KidFont`, and the `CloudGeometry` used by the weather icons are in `App.xaml`. Button icons are drawn inline in XAML as vector shapes, with no image assets.

## UX constraints

The audience is young children with a parent doing setup. User-facing strings should be short, cheerful, and non-technical, like the existing `FriendlyMessages` and the `"Oops! ..."` errors. Big buttons are `Focusable="False"` so a stray keypress doesn't trigger them.

## Runtime requirements (for debugging connection issues)

- Minecraft: "Require Encrypted Websockets" must be off, and cheats must be on for the world.
- `/connect` must be redone every time a world is opened.
- Some builds block loopback. Fix it with `CheckNetIsolation LoopbackExempt -a -n=Microsoft.MinecraftUWP_8wekyb3d8bbwe` from an admin terminal.
