namespace Pointframe.DesktopTestFixture;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bounds = ClientRectangle;
        using var brush = new SolidBrush(Color.FromArgb(235, 242, 255));
        e.Graphics.FillRectangle(brush, bounds);

        using var pen = new Pen(Color.MidnightBlue, 4);
        e.Graphics.DrawRectangle(pen, 24, 24, bounds.Width - 48, bounds.Height - 48);
        e.Graphics.DrawLine(pen, 24, 24, bounds.Width - 24, bounds.Height - 48);
        e.Graphics.DrawLine(pen, bounds.Width - 24, 24, 24, bounds.Height - 48);

        using var font = new Font(Font.FontFamily, 18, FontStyle.Bold);
        e.Graphics.DrawString("Pointframe external desktop fixture", font, Brushes.MidnightBlue, 48, 48);
    }
}
