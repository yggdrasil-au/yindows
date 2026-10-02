using System.Drawing;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using Cosmos.Kernel.System.Mouse;

using yggdrasilKernel.HAL;

namespace yggdrasilKernel.PreOs.Desktop;

public static class WindowManager {
    private static Canvas? _canvas;
    private static bool _isLoading = true;
    private static int _loadingTicks;
    private static int _lastMinute = -1;
    private static string _cachedTime = "00:00";

    private static readonly Color DesktopColor = Color.FromArgb(0, 90, 158);
    private static readonly Color TaskbarColor = Color.FromArgb(23, 23, 23);
    private static readonly Color StartButtonColor = Color.FromArgb(0, 120, 215);
    private static readonly Color StartMenuColor = Color.FromArgb(32, 32, 32);

    public static void BootIntoYindows() {
        _canvas = Canvas.GetFullScreen();
        _canvas.Clear(Color.Black);

        // Clamp the pointer to the actual screen resolution
        MouseManager.SetScreenSize(_canvas.Width, _canvas.Height);

        _canvas.Display();
    }

    public static void UpdateGui() {
        Canvas canvas = _canvas ?? throw new System.InvalidOperationException("Yindows has not been initialized.");

        if (_isLoading) {
            DrawLoadingScreen();
        } else {
            int width = canvas.Width;
            int height = canvas.Height;

            MouseHandler.Update(height);
            Render(canvas, width, height);

            DrawCursor(canvas, (int)MouseManager.X, (int)MouseManager.Y);
        }

        canvas.Display(); // Swap double buffer to screen
    }

    private static void DrawCursor(Canvas canvas, int mouseX, int mouseY) {
        for (int y = 0; y < CursorAsset.Height; y++) {
            for (int x = 0; x < CursorAsset.Width; x++) {
                int argb = CursorAsset.Pixels[y * CursorAsset.Width + x];
                byte alpha = (byte)((argb >> 24) & 0xFF);
                if (alpha > 128) { // Only draw visible pixels
                    canvas.DrawPoint(Color.FromArgb(argb), mouseX + x, mouseY + y);
                }
            }
        }
    }

    private static void DrawLoadingScreen() {
        Canvas canvas = _canvas ?? throw new System.InvalidOperationException("Yindows has not been initialized.");
        canvas.Clear(Color.Black);

        int screenWidth = canvas.Width;
        int screenHeight = canvas.Height;
        int centerX = screenWidth / 2;
        int centerY = screenHeight / 2;

        // Draw iconic 4-square Yindows logo
        int size = 40;
        int gap = 5;
        canvas.DrawFilledRectangle(StartButtonColor, centerX - size - gap, centerY - size - gap, size, size);
        canvas.DrawFilledRectangle(StartButtonColor, centerX + gap, centerY - size - gap, size, size);
        canvas.DrawFilledRectangle(StartButtonColor, centerX - size - gap, centerY + gap, size, size);
        canvas.DrawFilledRectangle(StartButtonColor, centerX + gap, centerY + gap, size, size);

        // Draw loading dots animation below the logo
        int dotY = centerY + 80;
        int dotCount = (_loadingTicks / 15) % 4;

        for (int i = 0; i < dotCount; i++) {
            canvas.DrawFilledRectangle(Color.White, centerX - 15 + (i * 15), dotY, 4, 4);
        }

        _loadingTicks++;

        if (_loadingTicks > 150) {
            _isLoading = false;
        }
    }

    public static void Render(Canvas canvas, int screenWidth, int screenHeight) {
        // 1. Desktop Background
        canvas.Clear(DesktopColor);

        // 2. Taskbar (Dark mode)
        int taskbarHeight = 40;
        int taskbarY = screenHeight - taskbarHeight;
        canvas.DrawFilledRectangle(TaskbarColor, 0, taskbarY, screenWidth, taskbarHeight);

        // 3. Start Button
        int startButtonWidth = 48;
        canvas.DrawFilledRectangle(StartButtonColor, 0, taskbarY, startButtonWidth, taskbarHeight);

        DrawStartButtonLogo(canvas, startButtonWidth, taskbarY, taskbarHeight);

        if (MouseHandler.IsStartMenuOpen) {
            int menuHeight = 300;
            int menuY = taskbarY - menuHeight;
            canvas.DrawFilledRectangle(StartMenuColor, 0, menuY, 250, menuHeight);
            canvas.DrawString("Yindows", PCScreenFont.DefaultFont, Color.White, 16, menuY + 18);
        }

        // 4. System Tray (Current Time)
        string timeStr = GetCurrentTime();

        // Dynamically calculate the text width so it perfectly aligns to the right edge
        int timeWidth = timeStr.Length * PCScreenFont.DefaultFont.Width;
        int rightMargin = 15; // Padding from the edge of the screen

        canvas.DrawString(timeStr, PCScreenFont.DefaultFont, Color.White, screenWidth - timeWidth - rightMargin, taskbarY + 12);
    }

    private static void DrawStartButtonLogo(Canvas canvas, int buttonWidth, int buttonY, int buttonHeight) {
        int logoX = (buttonWidth - StartMenuLogoAsset.Width) / 2;
        int logoY = buttonY + (buttonHeight - StartMenuLogoAsset.Height) / 2;

        for (int row = 0; row < StartMenuLogoAsset.Height; row++) {
            for (int column = 0; column < StartMenuLogoAsset.Width; column++) {
                int argb = StartMenuLogoAsset.Pixels[row * StartMenuLogoAsset.Width + column];
                int alpha = (int)((uint)argb >> 24);
                if (alpha == 0) {
                    continue;
                }

                int red = (argb >> 16) & 0xFF;
                int green = (argb >> 8) & 0xFF;
                int blue = argb & 0xFF;
                if (alpha < 255) {
                    int inverseAlpha = 255 - alpha;
                    red = (red * alpha + StartButtonColor.R * inverseAlpha + 127) / 255;
                    green = (green * alpha + StartButtonColor.G * inverseAlpha + 127) / 255;
                    blue = (blue * alpha + StartButtonColor.B * inverseAlpha + 127) / 255;
                }

                canvas.DrawPoint(Color.FromArgb(255, red, green, blue), logoX + column, logoY + row);
            }
        }
    }

    private static string GetCurrentTime() {
        return HAL.HardwareClock.GetFormattedTime();
    }
}