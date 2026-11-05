<div align="center">

<img src="docs/logo.png" width="80" alt="Snip">

# Snip

Screenshots, OCR, GIFs and videos for Windows. <kbd>Print Screen</kbd>, drag, <kbd>Ctrl</kbd>+<kbd>V</kbd>.

[**Download SnipSetup.exe**](https://github.com/ocauapaz/snip/releases/latest/download/SnipSetup.exe) · [portable Snip.exe](https://github.com/ocauapaz/snip/releases/latest/download/Snip.exe)

</div>

## Features

- **Image or text:** copy the selection as an image, or as text with the offline Windows OCR.
- **GIF or video:** record an area. When you stop, the file is already on the clipboard: paste it into a chat or a folder. Click the card to trim it and set its speed and size.
- **Sound:** turn on the speaker button to record what your PC plays (not the microphone). The recording is then a light MP4 (H.264 and AAC) instead of a GIF.
- **Editor:** crop, pen, highlighter, rectangle, arrow, pixelate, undo.
- **Auto-save:** every capture goes to `Pictures\Snip` and is deleted after 2 days.
- **Small:** 136 KB, lives in the tray, starts with Windows.
- **Setup:** `SnipSetup.exe` installs Snip for your user (no admin) in `%LOCALAPPDATA%\Programs\Snip`, with a Start menu shortcut, so Windows Search finds it. Remove it in **Settings** > **Apps**. `Snip.exe` alone still runs portable.

## Keys

| Key | Action |
| --- | --- |
| <kbd>Print Screen</kbd> | Capture (drag a box, or click for the full monitor); press again to start over |
| <kbd>Tab</kbd> | Image / Text / GIF |
| <kbd>Print Screen</kbd> while recording | Stop the recording and copy it |
| <kbd>Esc</kbd> | Cancel |
| <kbd>C</kbd> <kbd>P</kbd> <kbd>H</kbd> <kbd>R</kbd> <kbd>A</kbd> <kbd>B</kbd> | Editor tools |
| <kbd>Space</kbd> <kbd>←</kbd> <kbd>→</kbd> <kbd>I</kbd> <kbd>O</kbd> | Recording editor: play, step a frame, trim start, trim end |
| <kbd>Enter</kbd> / <kbd>Ctrl</kbd>+<kbd>S</kbd> | Copy / save |

> [!NOTE]
> The exes are not signed. If SmartScreen warns, click **More info** > **Run anyway**.

## Build

Windows 10/11 with the Windows SDK. Run `build.cmd` (it makes `Snip.exe` and `SnipSetup.exe`); `build-test.cmd` runs the self-tests.

## License

[MIT](LICENSE)
