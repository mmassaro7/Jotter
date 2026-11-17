using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Jotter;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The first plain launch owns the saved session. A window opened for a file never
        // claims it, so opening a file can't stop the normal Jotter window from restoring.
        bool ownsSession = false;
        Mutex? sessionMutex = null;
        if (MainForm.FindFileArg(args) == null)
            sessionMutex = new Mutex(true, @"Local\Jotter.SessionOwner", out ownsSession);
        Application.Run(new MainForm(args, ownsSession));
        sessionMutex?.Dispose();
    }
}

sealed class AppSettings
{
    public string FontFamily { get; set; } = "Consolas";
    public float FontSize { get; set; } = 11f;
    public FontStyle FontStyle { get; set; } = FontStyle.Regular;
    public bool WordWrap { get; set; } = true;
    public bool StatusBar { get; set; } = true;
    public string Theme { get; set; } = "System"; // Light | Dark | System
    public bool RestoreSession { get; set; } = true;
    public List<string> RecentFiles { get; set; } = new();
    public bool HasBounds { get; set; }
    public int WinX { get; set; }
    public int WinY { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public bool WinMax { get; set; }

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jotter");

    public static AppSettings Load()
    {
        try
        {
            var p = Path.Combine(Dir, "settings.json");
            if (File.Exists(p)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "settings.json"),
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

sealed class SessionTab
{
    public string? Path { get; set; }
    public string? Content { get; set; }
    public string Encoding { get; set; } = "UTF-8";
    public string Eol { get; set; } = "\r\n";
    public int SelStart { get; set; }
    public float Zoom { get; set; } = 1f;
}

sealed class SessionData
{
    public List<SessionTab> Tabs { get; set; } = new();
    public int Active { get; set; }
}

sealed class Document
{
    public Document(PlainEditor editor) { Editor = editor; }
    public PlainEditor Editor { get; }
    public string? FilePath;
    public string EncodingName = "UTF-8";
    public string Eol = "\r\n";
    public bool Dirty;
    public string Title => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);
}

static class TextCodec
{
    public static readonly string[] EncodingNames = { "UTF-8", "UTF-8 with BOM", "UTF-16 LE", "UTF-16 BE", "ANSI" };
    public static readonly (string Eol, string Name)[] Eols =
        { ("\r\n", "Windows (CRLF)"), ("\n", "Unix (LF)"), ("\r", "Macintosh (CR)") };

    public static (string Text, string Encoding) Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return (new UTF8Encoding(false).GetString(b, 3, b.Length - 3), "UTF-8 with BOM");
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return (Encoding.Unicode.GetString(b, 2, b.Length - 2), "UTF-16 LE");
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2), "UTF-16 BE");
        try { return (new UTF8Encoding(false, true).GetString(b), "UTF-8"); }
        catch (DecoderFallbackException) { return (Encoding.GetEncoding(1252).GetString(b), "ANSI"); }
    }

    public static Encoding GetEncoding(string name) => name switch
    {
        "UTF-8 with BOM" => new UTF8Encoding(true),
        "UTF-16 LE" => new UnicodeEncoding(false, true),
        "UTF-16 BE" => new UnicodeEncoding(true, true),
        "ANSI" => Encoding.GetEncoding(1252),
        _ => new UTF8Encoding(false),
    };

    public static string DetectEol(string t) =>
        t.Contains("\r\n") ? "\r\n" : t.Contains('\n') ? "\n" : t.Contains('\r') ? "\r" : "\r\n";

    public static string ToEditor(string t) => t.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string EolName(string eol) => Eols.First(e => e.Eol == eol).Name;
}

/// <summary>A RichTextBox that behaves like a plain-text editor.</summary>
sealed class PlainEditor : RichTextBox
{
    public PlainEditor()
    {
        BorderStyle = BorderStyle.None;
        AcceptsTab = true;
        DetectUrls = false;
        EnableAutoDragDrop = false;
        HideSelection = false;
        ScrollBars = RichTextBoxScrollBars.Both;
        MaxLength = int.MaxValue;
        Multiline = true;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (base.ProcessCmdKey(ref msg, keyData)) return true;
        if (keyData == (Keys.Shift | Keys.Insert) || keyData == (Keys.Control | Keys.Shift | Keys.V))
        {
            PastePlain();
            return true;
        }
        // Swallow RichEdit formatting shortcuts (alignment, line spacing, caps, sub/superscript).
        var k = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
        if (ctrl && (k is Keys.L or Keys.R or Keys.E or Keys.J or Keys.D1 or Keys.D2 or Keys.D5
                     or Keys.Oemplus || (shift && k == Keys.A)))
            return true;
        return false;
    }

    public void PastePlain()
    {
        if (Clipboard.ContainsText()) SelectedText = TextCodec.ToEditor(Clipboard.GetText());
    }

    public void CopyPlain()
    {
        if (SelectionLength > 0) Clipboard.SetText(SelectedText.Replace("\n", "\r\n"));
    }
}

sealed record Palette(bool Dark, Color Back, Color Surface, Color MenuBack, Color Text, Color Subtle,
    Color Border, Color Hover, Color Accent)
{
    public static readonly Palette Light = new(false,
        Color.FromArgb(243, 243, 243), Color.White, Color.FromArgb(249, 249, 249),
        Color.FromArgb(26, 26, 26), Color.FromArgb(96, 96, 96), Color.FromArgb(220, 220, 220),
        Color.FromArgb(228, 228, 228), Color.FromArgb(0, 103, 192));

    public static readonly Palette DarkPalette = new(true,
        Color.FromArgb(32, 32, 32), Color.FromArgb(40, 40, 40), Color.FromArgb(44, 44, 44),
        Color.FromArgb(240, 240, 240), Color.FromArgb(160, 160, 160), Color.FromArgb(60, 60, 60),
        Color.FromArgb(58, 58, 58), Color.FromArgb(76, 194, 255));
}

sealed class ThemedColors : ProfessionalColorTable
{
    readonly Palette p;
    public ThemedColors(Palette p) { this.p = p; UseSystemColors = false; }
    public override Color MenuStripGradientBegin => p.Back;
    public override Color MenuStripGradientEnd => p.Back;
    public override Color ToolStripDropDownBackground => p.MenuBack;
    public override Color ImageMarginGradientBegin => p.MenuBack;
    public override Color ImageMarginGradientMiddle => p.MenuBack;
    public override Color ImageMarginGradientEnd => p.MenuBack;
    public override Color MenuBorder => p.Border;
    public override Color MenuItemBorder => p.Hover;
    public override Color MenuItemSelected => p.Hover;
    public override Color MenuItemSelectedGradientBegin => p.Hover;
    public override Color MenuItemSelectedGradientEnd => p.Hover;
    public override Color MenuItemPressedGradientBegin => p.Hover;
    public override Color MenuItemPressedGradientMiddle => p.Hover;
    public override Color MenuItemPressedGradientEnd => p.Hover;
    public override Color SeparatorDark => p.Border;
    public override Color SeparatorLight => p.Border;
    public override Color StatusStripGradientBegin => p.Back;
    public override Color StatusStripGradientEnd => p.Back;
    public override Color ButtonSelectedHighlight => p.Hover;
    public override Color ButtonSelectedGradientBegin => p.Hover;
    public override Color ButtonSelectedGradientMiddle => p.Hover;
    public override Color ButtonSelectedGradientEnd => p.Hover;
    public override Color ButtonSelectedBorder => p.Hover;
    public override Color ButtonPressedGradientBegin => p.Hover;
    public override Color ButtonPressedGradientMiddle => p.Hover;
    public override Color ButtonPressedGradientEnd => p.Hover;
    public override Color ButtonPressedBorder => p.Hover;
    public override Color CheckBackground => p.MenuBack;
    public override Color CheckSelectedBackground => p.Hover;
    public override Color CheckPressedBackground => p.Hover;
}

sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    readonly Palette p;
    public ThemedRenderer(Palette p) : base(new ThemedColors(p)) { this.p = p; RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? p.Text : p.Subtle;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = p.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var pen = new Pen(p.Text, Math.Max(1.5f, r.Width / 10f));
        g.DrawLines(pen, new[]
        {
            new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
        });
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var pen = new Pen(p.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }
}

static class Native
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);

    public static void DarkTitleBar(Control c, bool dark)
    {
        if (!c.IsHandleCreated) return;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(c.Handle, 20, ref v, sizeof(int));
    }

    public static void ScrollbarTheme(Control c, bool dark)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    public static GraphicsPath Rounded(Rectangle r, int radius, bool topOnly)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        if (topOnly)
        {
            path.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
        }
        else
        {
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        }
        path.CloseFigure();
        return path;
    }
}
