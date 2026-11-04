<div align="center">

<img src="docs/logo.png" width="80" alt="Snip">

# Snip

Screenshots, OCR and GIFs for Windows. <kbd>Print Screen</kbd>, drag, <kbd>Ctrl</kbd>+<kbd>V</kbd>.

[**Download Snip.exe**](https://github.com/ocauapaz/snip/releases/latest/download/Snip.exe)

</div>

## Features

- **Image or text:** copy the selection as an image, or as text with the offline Windows OCR.
- **GIF:** record an area, then trim it, set its speed and size, and copy it as a file that pastes into chats.
- **Editor:** crop, pen, highlighter, rectangle, arrow, pixelate, undo.
- **Auto-save:** every capture goes to `Pictures\Snip` and is deleted after 2 days.
- **Small:** 108 KB, no installer, lives in the tray, starts with Windows.

## Keys

| Key | Action |
| --- | --- |
| <kbd>Print Screen</kbd> | Capture (drag a box, or click for the full monitor); press again to start over |
| <kbd>Tab</kbd> | Image / Text / GIF |
| <kbd>Print Screen</kbd> while recording | Stop the GIF and open its editor |
| <kbd>Esc</kbd> | Cancel |
| <kbd>C</kbd> <kbd>P</kbd> <kbd>H</kbd> <kbd>R</kbd> <kbd>A</kbd> <kbd>B</kbd> | Editor tools |
| <kbd>Space</kbd> <kbd>←</kbd> <kbd>→</kbd> <kbd>I</kbd> <kbd>O</kbd> | GIF editor: play, step a frame, trim start, trim end |
| <kbd>Enter</kbd> / <kbd>Ctrl</kbd>+<kbd>S</kbd> | Copy / save |

> [!NOTE]
> The exe is not signed. If SmartScreen warns, click **More info** > **Run anyway**.

## Build

Windows 10/11 with the Windows SDK. Run `build.cmd`; `build-test.cmd` runs the self-tests.

## License

[MIT](LICENSE)
