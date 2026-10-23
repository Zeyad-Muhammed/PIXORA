using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace PIXORA.App.Views;

/// <summary>Grid that exposes the protected cursor property so canvases can show cross/I-beam cursors.</summary>
public sealed partial class CursorGrid : Grid
{
    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}
