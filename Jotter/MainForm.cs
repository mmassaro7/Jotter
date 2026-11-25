using System.Diagnostics;
using System.Drawing.Printing;
using System.Text.Json;
using Microsoft.Win32;

namespace Jotter;

sealed class MainForm : Form
{
    const string AppName = "Jotter";

    readonly AppSettings settings;
    readonly bool ownsSession;
    readonly bool fileWindow;
    readonly MenuStrip menu = new() { Padding = new Padding(4, 2, 0, 2) };
    readonly Panel host = new() { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 0, 0) };
    readonly StatusStrip status = new() { SizingGrip = false };
    readonly ToolStripStatusLabel lblPos = new() { Padding = new Padding(8, 0, 8, 0) };
    readonly ToolStripStatusLabel lblChars = new() { Padding = new Padding(8, 0, 8, 0) };
    readonly ToolStripStatusLabel lblSpring = new() { Spring = true };
    readonly ToolStripStatusLabel lblZoom = new() { Padding = new Padding(8, 0, 8, 0) };
    readonly ToolStripDropDownButton btnEol = new() { ShowDropDownArrow = false, Padding = new Padding(8, 0, 8, 0) };
    readonly ToolStripDropDownButton btnEnc = new() { ShowDropDownArrow = false, Padding = new Padding(8, 0, 8, 0) };
    readonly List<Document> docs = new();
    readonly System.Windows.Forms.Timer statusTimer = new() { Interval = 50 };
    readonly System.Windows.Forms.Timer sessionTimer = new() { Interval = 4000 };

    ToolStripMenuItem miWrap = null!, miStatus = null!, miRecent = null!, miRestore = null!;
    ToolStripMenuItem miLight = null!, miDark = null!, miSystem = null!;
    ToolStripMenuItem miUndo = null!, miRedo = null!, miCut = null!, miCopy = null!, miDelete = null!;

    Document? cur;
    FindReplaceForm? findForm;
    Palette pal = Palette.Light;
    bool loading, sessionDirty;
    string lastFind = "";
    bool lastCase, lastWrap = true;

    static string SessionPath => Path.Combine(AppSettings.Dir, "session.json");

    public MainForm(string[] args, bool ownsSession)
    {
        var fileArg = FindFileArg(args);
        // A window opened for a specific file is throwaway: it never touches the saved
        // session, recent files or settings, and unsaved changes are discarded on close.
        fileWindow = fileArg != null;
        this.ownsSession = ownsSession && !fileWindow;
        settings = AppSettings.Load();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        Text = AppName;
        Icon = AppIcon.Create();
        MinimumSize = new Size(420, 300);
        Size = new Size(960, 680);
        AllowDrop = true;
        RestoreWindowBounds();

        BuildMenu();
        BuildStatus();
        Controls.Add(host);
        Controls.Add(menu);
        Controls.Add(status);
        MainMenuStrip = menu;

        statusTimer.Tick += (s, e) => { statusTimer.Stop(); UpdateStatus(); };
        sessionTimer.Tick += (s, e) =>
        {
            if (sessionDirty && ownsSession && settings.RestoreSession) { sessionDirty = false; SaveSession(); }
        };
        sessionTimer.Start();
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        if (this.ownsSession && settings.RestoreSession) RestoreSession();
        if (docs.Count == 0) NewTab();
        if (fileArg != null) OpenFile(fileArg);

        status.Visible = settings.StatusBar;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    PlainEditor Ed => cur!.Editor;

    // ───────────────────────── Menus ─────────────────────────

    static ToolStripMenuItem Item(string text, Keys keys, Action action, string? display = null)
    {
        var m = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        if (display != null) m.ShortcutKeyDisplayString = display;
        m.Click += (s, e) => action();
        return m;
    }

    Action OnEditor(Action<PlainEditor> f) => () => { if (cur != null) f(cur.Editor); };

    void BuildMenu()
    {
        miRecent = new ToolStripMenuItem("Recent files");
        miRecent.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        miRecent.DropDownOpening += (s, e) => BuildRecentMenu();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("New", Keys.Control | Keys.N, NewDocument),
            Item("New window", Keys.Control | Keys.Shift | Keys.N, NewWindow),
            Item("Open...", Keys.Control | Keys.O, OpenDialog),
            miRecent,
            new ToolStripSeparator(),
            Item("Save", Keys.Control | Keys.S, () => { if (cur != null) Save(cur); }),
            Item("Save as...", Keys.Control | Keys.Shift | Keys.S, () => { if (cur != null) SaveAs(cur); }),
            new ToolStripSeparator(),
            Item("Print...", Keys.Control | Keys.P, Print),
            new ToolStripSeparator(),
            Item("Close window", Keys.Control | Keys.W, Close),
            Item("Exit", Keys.None, Close),
        });

        miUndo = Item("Undo", Keys.Control | Keys.Z, OnEditor(ed => ed.Undo()));
        miRedo = Item("Redo", Keys.Control | Keys.Y, OnEditor(ed => ed.Redo()));
        miCut = Item("Cut", Keys.Control | Keys.X, OnEditor(ed => { ed.CopyPlain(); if (ed.SelectionLength > 0) ed.SelectedText = ""; }));
        miCopy = Item("Copy", Keys.Control | Keys.C, OnEditor(ed => ed.CopyPlain()));
        miDelete = Item("Delete", Keys.None, OnEditor(ed =>
        {
            if (ed.SelectionLength == 0 && ed.SelectionStart < ed.TextLength) ed.SelectionLength = 1;
            ed.SelectedText = "";
        }), "Del");

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.AddRange(new ToolStripItem[]
        {
            miUndo, miRedo, new ToolStripSeparator(),
            miCut, miCopy,
            Item("Paste", Keys.Control | Keys.V, OnEditor(ed => ed.PastePlain())),
            miDelete,
            new ToolStripSeparator(),
            Item("Find...", Keys.Control | Keys.F, () => ShowFind(false)),
            Item("Find next", Keys.F3, () => FindAgain(up: false)),
            Item("Find previous", Keys.Shift | Keys.F3, () => FindAgain(up: true)),
            Item("Replace...", Keys.Control | Keys.H, () => ShowFind(true)),
            Item("Go to...", Keys.Control | Keys.G, GoToLine),
            new ToolStripSeparator(),
            Item("Select all", Keys.Control | Keys.A, OnEditor(ed => ed.SelectAll())),
            Item("Time/Date", Keys.F5, OnEditor(ed =>
            {
                var now = DateTime.Now;
                ed.SelectedText = $"{now.ToShortTimeString()} {now.ToShortDateString()}";
            })),
        });
        // Enable/disable only while the menu is open, so shortcuts keep working.
        edit.DropDownOpening += (s, e) =>
        {
            if (cur == null) return;
            miUndo.Enabled = Ed.CanUndo;
            miRedo.Enabled = Ed.CanRedo;
            miCut.Enabled = miCopy.Enabled = miDelete.Enabled = Ed.SelectionLength > 0;
        };
        edit.DropDownClosed += (s, e) => miUndo.Enabled = miRedo.Enabled = miCut.Enabled = miCopy.Enabled = miDelete.Enabled = true;

        miWrap = Item("Word wrap", Keys.None, ToggleWrap);
        miWrap.Checked = settings.WordWrap;
        var format = new ToolStripMenuItem("F&ormat");
        format.DropDownItems.AddRange(new ToolStripItem[] { miWrap, Item("Font...", Keys.None, ChooseFont) });

        var zoom = new ToolStripMenuItem("Zoom");
        zoom.DropDownItems.AddRange(new ToolStripItem[]
        {
            Item("Zoom in", Keys.Control | Keys.Oemplus, () => ZoomBy(0.1f), "Ctrl+Plus"),
            Item("Zoom out", Keys.Control | Keys.OemMinus, () => ZoomBy(-0.1f), "Ctrl+Minus"),
            Item("Restore default zoom", Keys.Control | Keys.D0, () => SetZoom(1f), "Ctrl+0"),
        });
        miStatus = Item("Status bar", Keys.None, () =>
        {
            settings.StatusBar = !settings.StatusBar;
            status.Visible = miStatus.Checked = settings.StatusBar;
            settings.Save();
        });
        miStatus.Checked = settings.StatusBar;
        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.AddRange(new ToolStripItem[] { zoom, miStatus });

        miLight = Item("Light", Keys.None, () => SetTheme("Light"));
        miDark = Item("Dark", Keys.None, () => SetTheme("Dark"));
        miSystem = Item("Use system setting", Keys.None, () => SetTheme("System"));
        var theme = new ToolStripMenuItem("Theme");
        theme.DropDownItems.AddRange(new ToolStripItem[] { miLight, miDark, miSystem });
        miRestore = Item("Reopen unsaved work from last session", Keys.None, () =>
        {
            settings.RestoreSession = miRestore.Checked = !settings.RestoreSession;
            settings.Save();
        });
        miRestore.Checked = settings.RestoreSession;
        var settingsMenu = new ToolStripMenuItem("&Settings");
        settingsMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            theme, miRestore, new ToolStripSeparator(),
            Item($"About {AppName}", Keys.None, () => MessageBox.Show(this,
                $"{AppName} 1.0\n\nA lightweight tabbed text editor.\nSettings and session are stored in:\n{AppSettings.Dir}",
                $"About {AppName}", MessageBoxButtons.OK, MessageBoxIcon.Information)),
        });

        menu.Items.AddRange(new ToolStripItem[] { file, edit, format, view, settingsMenu });
    }

    void BuildRecentMenu()
    {
        miRecent.DropDownItems.Clear();
        if (settings.RecentFiles.Count == 0)
        {
            miRecent.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
            return;
        }
        int n = 1;
        foreach (var path in settings.RecentFiles.ToList())
        {
            var p = path;
            miRecent.DropDownItems.Add(Item($"&{n++} {p}", Keys.None, () =>
            {
                if (File.Exists(p)) OpenFile(p);
                else
                {
                    MessageBox.Show(this, $"\"{p}\" no longer exists.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    settings.RecentFiles.Remove(p);
                    settings.Save();
                }
            }));
        }
        miRecent.DropDownItems.Add(new ToolStripSeparator());
        miRecent.DropDownItems.Add(Item("Clear recent files", Keys.None, () => { settings.RecentFiles.Clear(); settings.Save(); }));
    }

    void BuildStatus()
    {
        foreach (var name in TextCodec.EncodingNames)
        {
            var n = name;
            btnEnc.DropDownItems.Add(Item(n, Keys.None, () =>
            {
                if (cur == null || cur.EncodingName == n) return;
                cur.EncodingName = n;
                MarkDirty(cur);
            }));
        }
        foreach (var (eol, name) in TextCodec.Eols)
        {
            var e = eol;
            btnEol.DropDownItems.Add(Item(name, Keys.None, () =>
            {
                if (cur == null || cur.Eol == e) return;
                cur.Eol = e;
                MarkDirty(cur);
            }));
        }
        btnEnc.ToolTipText = "Encoding used when saving";
        btnEol.ToolTipText = "Line endings used when saving";
        status.Items.AddRange(new ToolStripItem[]
        {
            lblPos, new ToolStripSeparator(), lblChars, lblSpring,
            lblZoom, new ToolStripSeparator(), btnEol, new ToolStripSeparator(), btnEnc,
        });
    }

    // ───────────────────────── Documents & tabs ─────────────────────────

    Document CreateDoc()
    {
        var ed = new PlainEditor { Dock = DockStyle.Fill, Visible = false, WordWrap = settings.WordWrap, Font = EditorFont() };
        var d = new Document(ed);
        ed.TextChanged += (s, e) =>
        {
            if (loading) return;
            if (ed.TextLength == 0) d.Started = null;
            else d.Started ??= DateTime.Now;
            if (!d.Dirty) MarkDirty(d);
            sessionDirty = true;
            ScheduleStatus();
        };
        ed.SelectionChanged += (s, e) => ScheduleStatus();
        ed.MouseWheel += (s, e) => { if ((ModifierKeys & Keys.Control) != 0) ScheduleStatus(); };
        ed.AllowDrop = true;
        ed.DragEnter += OnDragEnter;
        ed.DragDrop += OnDragDrop;
        ed.HandleCreated += (s, e) => Native.ScrollbarTheme(ed, pal.Dark);
        ThemeEditor(ed);
        host.Controls.Add(ed);
        docs.Add(d);
        return d;
    }

    void MarkDirty(Document d)
    {
        d.Dirty = true;
        sessionDirty = true;
        RefreshChrome();
    }

    Document NewTab()
    {
        var d = CreateDoc();
        Activate(d);
        return d;
    }

    void Activate(Document d)
    {
        if (cur != null && cur != d) cur.Editor.Visible = false;
        cur = d;
        d.Editor.Visible = true;
        d.Editor.BringToFront();
        if (IsHandleCreated) d.Editor.Focus();
        RefreshChrome();
        UpdateStatus();
    }

    void RefreshChrome()
    {
        if (cur == null) return;
        Text = $"{(cur.Dirty ? "*" : "")}{cur.Title} - {AppName}";
    }

    /// <summary>File > New: replaces the current text with an empty untitled document.</summary>
    void NewDocument()
    {
        if (cur == null || !ConfirmClose(cur)) return;
        loading = true;
        cur.Editor.Clear();
        cur.Editor.ClearUndo();
        loading = false;
        cur.FilePath = null;
        cur.Started = null;
        cur.EncodingName = "UTF-8";
        cur.Eol = "\r\n";
        cur.Dirty = false;
        sessionDirty = true;
        RefreshChrome();
        UpdateStatus();
    }

    bool ConfirmClose(Document d)
    {
        if (!d.Dirty) return true;
        Activate(d);
        var r = MessageBox.Show(this, $"Do you want to save changes to {d.Title}?", AppName,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return r switch
        {
            DialogResult.Yes => Save(d),
            DialogResult.No => true,
            _ => false,
        };
    }

    void NewWindow()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            psi.ArgumentList.Add("--new-window");
            Process.Start(psi);
        }
        catch (Exception ex) { Error("Could not open a new window.", ex); }
    }

    // ───────────────────────── File I/O ─────────────────────────

    void OpenDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*",
            FilterIndex = 2,
        };
        if (cur?.FilePath != null) dlg.InitialDirectory = Path.GetDirectoryName(cur.FilePath);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        OpenFile(dlg.FileName);
    }

    /// <summary>Opens a file in place of the current document, asking to save changes first.</summary>
    void OpenFile(string path)
    {
        if (cur == null || !ConfirmClose(cur)) return;
        if (!LoadInto(cur, path)) return;
        sessionDirty = true;
        RefreshChrome();
        UpdateStatus();
    }

    bool LoadInto(Document d, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 64L * 1024 * 1024 &&
                MessageBox.Show(this, $"{info.Name} is {info.Length / (1024 * 1024)} MB. Opening it may be slow. Continue?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            var (text, enc) = TextCodec.Decode(File.ReadAllBytes(path));
            d.Eol = TextCodec.DetectEol(text);
            d.EncodingName = enc;
            loading = true;
            d.Editor.Text = TextCodec.ToEditor(text);
            d.Editor.Select(0, 0);
            d.Editor.ClearUndo();
            loading = false;
            d.FilePath = path;
            d.Dirty = false;
            AddRecent(path);
            return true;
        }
        catch (Exception ex)
        {
            loading = false;
            Error($"Could not open {Path.GetFileName(path)}.", ex);
            return false;
        }
    }

    bool Save(Document d) => d.FilePath == null ? SaveAs(d) : WriteDoc(d, d.FilePath);

    bool SaveAs(Document d)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Text documents (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = "txt",
            AddExtension = true,
            FileName = d.FilePath != null ? Path.GetFileName(d.FilePath) : "Untitled.txt",
        };
        if (d.FilePath != null) dlg.InitialDirectory = Path.GetDirectoryName(d.FilePath);
        return dlg.ShowDialog(this) == DialogResult.OK && WriteDoc(d, dlg.FileName);
    }

    bool WriteDoc(Document d, string path)
    {
        try
        {
            var text = TextCodec.ToEditor(d.Editor.Text).Replace("\n", d.Eol);
            File.WriteAllText(path, text, TextCodec.GetEncoding(d.EncodingName));
            d.FilePath = path;
            d.Dirty = false;
            AddRecent(path);
            sessionDirty = true;
            RefreshChrome();
            return true;
        }
        catch (Exception ex)
        {
            Error($"Could not save {Path.GetFileName(path)}.", ex);
            return false;
        }
    }

    /// <summary>The first command-line argument that names an existing file, if any.</summary>
    public static string? FindFileArg(string[] args)
    {
        foreach (var a in args.Where(a => !a.StartsWith("--")))
        {
            try
            {
                var full = Path.GetFullPath(a);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    void AddRecent(string path)
    {
        if (fileWindow) return;
        settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, path);
        if (settings.RecentFiles.Count > 10) settings.RecentFiles.RemoveRange(10, settings.RecentFiles.Count - 10);
        settings.Save();
    }

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
        {
            var f = files.FirstOrDefault(File.Exists);
            if (f != null) BeginInvoke(() => OpenFile(f)); // after the drop completes, so a save prompt can show
        }
    }

    void Print()
    {
        if (cur == null) return;
        using var pd = new PrintDocument { DocumentName = cur.Title };
        using var dlg = new PrintDialog { Document = pd, UseEXDialog = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string remaining = Ed.Text.Replace("\t", "    ");
        using var font = EditorFont();
        using var fmt = new StringFormat(StringFormatFlags.LineLimit);
        pd.PrintPage += (s, e) =>
        {
            var area = e.MarginBounds;
            e.Graphics!.MeasureString(remaining, font, area.Size, fmt, out int chars, out _);
            if (chars == 0 && remaining.Length > 0) chars = remaining.Length;
            e.Graphics.DrawString(remaining[..chars], font, Brushes.Black, area, fmt);
            remaining = remaining[chars..];
            e.HasMorePages = remaining.Length > 0;
        };
        try { pd.Print(); }
        catch (Exception ex) { Error("Printing failed.", ex); }
    }

    // ───────────────────────── Find / Replace / Go to ─────────────────────────

    void ShowFind(bool replace)
    {
        if (cur == null) return;
        findForm ??= new FindReplaceForm(this);
        findForm.ApplyTheme(pal);
        findForm.ShowFor(replace, Ed.SelectedText.Length > 0 ? Ed.SelectedText : lastFind);
    }

    void FindAgain(bool up)
    {
        if (lastFind.Length == 0) ShowFind(false);
        else FindNext(lastFind, lastCase, up, lastWrap);
    }

    IWin32Window DialogOwner => findForm is { Visible: true } ? findForm : this;

    public bool FindNext(string term, bool matchCase, bool up, bool wrap, bool quiet = false)
    {
        if (cur == null || term.Length == 0) return false;
        lastFind = term; lastCase = matchCase; lastWrap = wrap;
        var ed = Ed;
        string text = ed.Text;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int idx;
        if (!up)
        {
            int start = Math.Min(ed.SelectionStart + ed.SelectionLength, text.Length);
            idx = text.IndexOf(term, start, cmp);
            if (idx < 0 && wrap) idx = text.IndexOf(term, cmp);
        }
        else
        {
            idx = text[..Math.Min(ed.SelectionStart, text.Length)].LastIndexOf(term, cmp);
            if (idx < 0 && wrap) idx = text.LastIndexOf(term, cmp);
        }
        if (idx < 0)
        {
            if (!quiet) MessageBox.Show(DialogOwner, $"Cannot find \"{term}\"", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        ed.Select(idx, term.Length);
        ed.ScrollToCaret();
        return true;
    }

    public void ReplaceOne(string term, string replacement, bool matchCase, bool up, bool wrap)
    {
        if (cur == null || term.Length == 0) return;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(Ed.SelectedText, term, cmp)) Ed.SelectedText = replacement;
        FindNext(term, matchCase, up, wrap);
    }

    public void ReplaceAll(string term, string replacement, bool matchCase)
    {
        if (cur == null || term.Length == 0) return;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = Ed.Text;
        var sb = new System.Text.StringBuilder(text.Length);
        int pos = 0, count = 0;
        while (true)
        {
            int idx = text.IndexOf(term, pos, cmp);
            if (idx < 0) break;
            sb.Append(text, pos, idx - pos).Append(replacement);
            pos = idx + term.Length;
            count++;
        }
        if (count > 0)
        {
            sb.Append(text, pos, text.Length - pos);
            Ed.SelectAll();
            Ed.SelectedText = sb.ToString(); // keeps it undoable
            Ed.Select(0, 0);
        }
        MessageBox.Show(DialogOwner, count == 0 ? $"Cannot find \"{term}\"" : $"Replaced {count:N0} occurrence{(count == 1 ? "" : "s")}.",
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void GoToLine()
    {
        if (cur == null) return;
        string text = Ed.Text;
        int totalLines = text.AsSpan().Count('\n') + 1;
        int curLine = text.AsSpan(0, Math.Min(Ed.SelectionStart, text.Length)).Count('\n') + 1;

        using var f = new Form
        {
            Text = "Go to line", FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            AutoScaleDimensions = new SizeF(96F, 96F), AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 9f), ClientSize = new Size(290, 112),
            BackColor = pal.Back, ForeColor = pal.Text,
        };
        var lbl = new Label { Text = $"Line number (1 – {totalLines:N0}):", AutoSize = true, Location = new Point(12, 12) };
        var tb = new TextBox { Location = new Point(12, 36), Width = 266, Text = curLine.ToString(), BackColor = pal.Surface, ForeColor = pal.Text };
        var ok = new Button { Text = "Go to", DialogResult = DialogResult.OK, Location = new Point(110, 74), Size = new Size(80, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(198, 74), Size = new Size(80, 28) };
        Theming.StyleButton(ok, pal);
        Theming.StyleButton(cancel, pal);
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        f.HandleCreated += (s, e) => Native.DarkTitleBar(f, pal.Dark);
        f.Shown += (s, e) => tb.SelectAll();
        if (f.ShowDialog(this) != DialogResult.OK) return;

        if (!int.TryParse(tb.Text.Trim(), out int n) || n < 1 || n > totalLines)
        {
            MessageBox.Show(this, "The line number is beyond the total number of lines.", $"{AppName} - Go to line",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        int idx = 0;
        for (int line = 1; line < n; line++) idx = text.IndexOf('\n', idx) + 1;
        Ed.Select(idx, 0);
        Ed.ScrollToCaret();
    }

    // ───────────────────────── View / format ─────────────────────────

    Font EditorFont()
    {
        try { return new Font(settings.FontFamily, settings.FontSize, settings.FontStyle); }
        catch { return new Font("Consolas", 11f); }
    }

    void ChooseFont()
    {
        using var current = EditorFont();
        using var dlg = new FontDialog { Font = current, ShowEffects = false, FontMustExist = true, AllowVerticalFonts = false };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        settings.FontFamily = dlg.Font.FontFamily.Name;
        settings.FontSize = dlg.Font.Size;
        settings.FontStyle = dlg.Font.Style & (FontStyle.Bold | FontStyle.Italic);
        settings.Save();
        loading = true;
        foreach (var d in docs)
        {
            float z = d.Editor.ZoomFactor;
            d.Editor.Font = EditorFont();
            _ = d.Editor.ZoomFactor; // resync cached zoom after the font reset it
            d.Editor.ZoomFactor = z;
            ThemeEditor(d.Editor);
        }
        loading = false;
        UpdateStatus();
    }

    void ToggleWrap()
    {
        settings.WordWrap = miWrap.Checked = !settings.WordWrap;
        settings.Save();
        loading = true;
        foreach (var d in docs) d.Editor.WordWrap = settings.WordWrap;
        loading = false;
    }

    void ZoomBy(float delta)
    {
        if (cur != null) SetZoom(Ed.ZoomFactor + delta);
    }

    void SetZoom(float z)
    {
        if (cur == null) return;
        z = Math.Clamp(MathF.Round(z * 10f) / 10f, 0.2f, 5f);
        _ = Ed.ZoomFactor;
        Ed.ZoomFactor = z;
        UpdateStatus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Add: ZoomBy(0.1f); return true;
            case Keys.Control | Keys.Subtract: ZoomBy(-0.1f); return true;
            case Keys.Control | Keys.NumPad0: SetZoom(1f); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void ScheduleStatus()
    {
        statusTimer.Stop();
        statusTimer.Start();
    }

    void UpdateStatus()
    {
        if (cur == null) return;
        var ed = cur.Editor;
        string text = ed.Text;
        int pos = Math.Min(ed.SelectionStart, text.Length);
        var before = text.AsSpan(0, pos);
        int line = before.Count('\n') + 1;
        int col = pos - before.LastIndexOf('\n');
        lblPos.Text = $"Ln {line:N0}, Col {col:N0}";
        lblChars.Text = ed.SelectionLength > 0
            ? $"{ed.SelectionLength:N0} of {text.Length:N0} characters"
            : $"{text.Length:N0} character{(text.Length == 1 ? "" : "s")}";
        lblZoom.Text = $"{Math.Round(ed.ZoomFactor * 100)}%";
        btnEol.Text = TextCodec.EolName(cur.Eol);
        btnEnc.Text = cur.EncodingName;
        foreach (ToolStripMenuItem i in btnEol.DropDownItems) i.Checked = i.Text == btnEol.Text;
        foreach (ToolStripMenuItem i in btnEnc.DropDownItems) i.Checked = i.Text == btnEnc.Text;
    }

    // ───────────────────────── Theme ─────────────────────────

    static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    void SetTheme(string theme)
    {
        settings.Theme = theme;
        settings.Save();
        ApplyTheme();
    }

    void ApplyTheme()
    {
        bool dark = settings.Theme == "Dark" || (settings.Theme == "System" && SystemIsDark());
        pal = dark ? Palette.DarkPalette : Palette.Light;
        BackColor = pal.Back;
        host.BackColor = pal.Surface;
        var renderer = new ThemedRenderer(pal);
        menu.Renderer = renderer;
        status.Renderer = renderer;
        menu.BackColor = status.BackColor = pal.Back;
        menu.ForeColor = status.ForeColor = pal.Text;
        bool l = loading;
        loading = true;
        foreach (var d in docs) ThemeEditor(d.Editor);
        loading = l;
        miLight.Checked = settings.Theme == "Light";
        miDark.Checked = settings.Theme == "Dark";
        miSystem.Checked = settings.Theme == "System";
        findForm?.ApplyTheme(pal);
        Native.DarkTitleBar(this, pal.Dark);
        Invalidate(true);
    }

    void ThemeEditor(PlainEditor ed)
    {
        bool l = loading;
        loading = true;
        ed.BackColor = pal.Surface;
        ed.ForeColor = pal.Text;
        Native.ScrollbarTheme(ed, pal.Dark);
        loading = l;
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (settings.Theme == "System" && IsHandleCreated) BeginInvoke(ApplyTheme);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(this, pal.Dark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        cur?.Editor.Focus();
        cur?.Editor.ScrollToCaret();
    }

    // ───────────────────────── Session & window state ─────────────────────────

    void SaveSession()
    {
        try
        {
            var s = new SessionData();
            foreach (var d in docs)
            {
                // Only unsaved text carries over. Once you save it to a file, the next start is empty.
                if (!d.Dirty) continue;
                if (d == cur) s.Active = s.Tabs.Count;
                string content = d.Editor.Text;
                int selStart = d.Editor.SelectionStart;
                // Nothing typed after this run's reopen divider: drop the divider so it isn't kept.
                if (d == dividerDoc && content == dividerFullText && dividerBaseText != null)
                {
                    content = dividerBaseText;
                    selStart = Math.Min(selStart, content.Length);
                }
                s.Tabs.Add(new SessionTab
                {
                    Path = d.FilePath,
                    Content = content,
                    Encoding = d.EncodingName,
                    Eol = d.Eol,
                    SelStart = selStart,
                    Zoom = d.Editor.ZoomFactor,
                    Started = d.Started,
                });
            }
            Directory.CreateDirectory(AppSettings.Dir);
            var tmp = SessionPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, SessionPath, overwrite: true);
        }
        catch { }
    }

    void RestoreSession()
    {
        SessionData? s;
        try { s = File.Exists(SessionPath) ? JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath)) : null; }
        catch { s = null; }
        if (s == null || s.Tabs.Count == 0) return;

        // One document per window: restore the document that was last in front.
        var activeTab = s.Tabs[Math.Clamp(s.Active, 0, s.Tabs.Count - 1)];
        s.Tabs = new List<SessionTab> { activeTab };
        s.Active = 0;

        var restored = new List<Document?>();
        foreach (var t in s.Tabs)
        {
            Document? d = null;
            if (t.Content != null)
            {
                d = CreateDoc();
                loading = true;
                d.Editor.Text = t.Content;
                d.Editor.ClearUndo();
                loading = false;
                d.FilePath = t.Path;
                d.Dirty = true;
                d.EncodingName = t.Encoding;
                d.Eol = t.Eol;
                d.Started = t.Started;
                // The original text gets a date/time header of its own at the top.
                if (t.Content.Length > 0 && !StartsWithHeader(t.Content))
                {
                    DateTime when = t.Started ?? SafeLastWrite(SessionPath);
                    loading = true;
                    d.Editor.Text = Header(when) + "\n\n" + t.Content;
                    d.Editor.ClearUndo();
                    loading = false;
                    t.SelStart += d.Editor.TextLength - t.Content.Length;
                }
            }
            // A tab with no unsaved content was saved to its file: start empty instead of reopening it.
            if (d != null)
            {
                d.Editor.Select(Math.Min(t.SelStart, d.Editor.TextLength), 0);
                if (t.Zoom > 0 && Math.Abs(t.Zoom - 1f) > 0.01f) d.Editor.ZoomFactor = t.Zoom;
            }
            restored.Add(d);
        }
        var active = s.Active >= 0 && s.Active < restored.Count ? restored[s.Active] : null;
        active ??= docs.LastOrDefault();
        if (active != null)
        {
            AppendReopenDivider(active);
            Activate(active);
        }
    }

    // The text before and after this run's reopen divider was added, so an untouched divider can be dropped on close.
    Document? dividerDoc;
    string? dividerBaseText, dividerFullText;

    /// <summary>Adds a "---------date time------------" line at the end of restored text to mark a reopen.</summary>
    void AppendReopenDivider(Document d)
    {
        var ed = d.Editor;
        if (ed.TextLength == 0) return;
        string divider = Header(DateTime.Now);
        string text = ed.Text;
        dividerDoc = d;
        dividerBaseText = text;
        // End the previous text's line, then leave two blank lines before the divider
        // and one blank line between the divider and the new text.
        string prefix = (text.EndsWith('\n') ? "" : "\n") + "\n\n";
        ed.Select(ed.TextLength, 0);
        ed.SelectedText = prefix + divider + "\n\n";
        ed.Select(ed.TextLength, 0);
        dividerFullText = ed.Text;
        if (!d.Dirty) MarkDirty(d); // TextChanged already marks it in most cases; this keeps it consistent
        sessionDirty = true;
    }

    static string Header(DateTime when)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"---------{when.ToString("M/d/yyyy", inv)} {when.ToString("h:mm tt", inv)}------------";
    }

    static readonly System.Text.RegularExpressions.Regex HeaderLine =
        new(@"^-{9}\d{1,2}/\d{1,2}/\d{4} \d{1,2}:\d{2} [AP]M-{12}\r?$");

    static bool StartsWithHeader(string text)
    {
        int nl = text.IndexOf('\n');
        return HeaderLine.IsMatch(nl < 0 ? text : text[..nl]);
    }

    static DateTime SafeLastWrite(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.Now; }
    }

    void RestoreWindowBounds()
    {
        if (!ownsSession || !settings.HasBounds) return;
        var r = new Rectangle(settings.WinX, settings.WinY, settings.WinW, settings.WinH);
        if (r.Width < 300 || r.Height < 200 || !Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (settings.WinMax) WindowState = FormWindowState.Maximized;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;
        if (fileWindow)
        {
            // Ask about unsaved changes (Save / Don't Save / Cancel), but never touch the
            // saved session or settings, so the normal Jotter window is unaffected.
            foreach (var d in docs.ToList())
            {
                if (!ConfirmClose(d)) { e.Cancel = true; return; }
            }
            return;
        }

        if (ownsSession && settings.RestoreSession)
        {
            SaveSession(); // unsaved work is kept and restored next time
        }
        else
        {
            foreach (var d in docs.ToList())
            {
                if (!ConfirmClose(d)) { e.Cancel = true; return; }
            }
            if (ownsSession) { try { File.Delete(SessionPath); } catch { } }
        }

        if (ownsSession)
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.HasBounds = true;
            settings.WinX = b.X; settings.WinY = b.Y; settings.WinW = b.Width; settings.WinH = b.Height;
            settings.WinMax = WindowState == FormWindowState.Maximized;
        }
        settings.Save();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        sessionTimer.Stop();
        base.OnFormClosed(e);
    }

    void Error(string message, Exception ex) =>
        MessageBox.Show(this, $"{message}\n\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
}

/// <summary>Draws the app's notepad-and-pencil icon at runtime.</summary>
static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var page = Native.Rounded(new Rectangle(8, 6, 44, 54), 7, topOnly: false);
            using (var b = new SolidBrush(Color.FromArgb(255, 255, 255))) g.FillPath(b, page);
            using (var pen = new Pen(Color.FromArgb(0, 103, 192), 3f)) g.DrawPath(pen, page);
            using (var top = new SolidBrush(Color.FromArgb(0, 103, 192))) g.FillRectangle(top, 9, 7, 42, 9);
            using (var lines = new Pen(Color.FromArgb(150, 160, 175), 3f))
                for (int y = 26; y <= 50; y += 8) g.DrawLine(lines, 16, y, 44, y);
            using var pencil = new Pen(Color.FromArgb(255, 185, 0), 7f) { EndCap = System.Drawing.Drawing2D.LineCap.Triangle };
            g.DrawLine(pencil, 58, 22, 36, 50);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
