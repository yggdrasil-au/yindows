using Cosmos.Kernel.System.Mouse;

namespace yggdrasilKernel.PreOs.Desktop;

public static class MouseHandler {
    private static bool _wasLeftPressed = false;
    public static bool IsStartMenuOpen { get; private set; } = false;

    public static void Update(int screenHeight) {
        int mouseX = (int)MouseManager.X;
        int mouseY = (int)MouseManager.Y;
        bool isLeftPressed = MouseManager.LeftButton;

        // Trigger only on the initial press (rising edge)
        if (isLeftPressed && !_wasLeftPressed) {
            if (mouseX >= 0 && mouseX <= 48 && mouseY >= screenHeight - 40) {
                ToggleStartMenu();
            } else if (IsStartMenuOpen && (mouseX > 250 || mouseY < screenHeight - 340)) {
                // Close start menu if clicking outside of it
                IsStartMenuOpen = false;
            }
        }

        _wasLeftPressed = isLeftPressed;
    }

    private static void ToggleStartMenu() {
        IsStartMenuOpen = !IsStartMenuOpen;
    }
}