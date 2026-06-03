namespace Recorder;

public sealed class TrayHostForm : Form
{
    public TrayHostForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Opacity = 0;
        Size = new Size(0, 0);
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false);
    }
}
