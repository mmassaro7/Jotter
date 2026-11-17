namespace Jotter;

/// <summary>Modeless Find / Replace window.</summary>
sealed class FindReplaceForm : Form
{
    readonly MainForm main;
    readonly Label lblFind = new() { Text = "Find what:", AutoSize = true, Location = new Point(12, 16) };
    readonly Label lblReplace = new() { Text = "Replace with:", AutoSize = true, Location = new Point(12, 48) };
    readonly TextBox txtFind = new() { Location = new Point(100, 12), Width = 222 };
    readonly TextBox txtReplace = new() { Location = new Point(100, 44), Width = 222 };
    readonly CheckBox chkCase = new() { Text = "Match case", AutoSize = true, Location = new Point(12, 84) };
    readonly CheckBox chkWrap = new() { Text = "Wrap around", AutoSize = true, Location = new Point(12, 110), Checked = true };
    readonly RadioButton rbUp = new() { Text = "Up", AutoSize = true, Location = new Point(150, 84) };
    readonly RadioButton rbDown = new() { Text = "Down", AutoSize = true, Location = new Point(150, 110), Checked = true };
    readonly Button btnNext = new() { Text = "Find next", Location = new Point(334, 10), Size = new Size(96, 28) };
    readonly Button btnReplace = new() { Text = "Replace", Location = new Point(334, 44), Size = new Size(96, 28) };
    readonly Button btnAll = new() { Text = "Replace all", Location = new Point(334, 78), Size = new Size(96, 28) };
    readonly Button btnClose = new() { Text = "Cancel", Location = new Point(334, 112), Size = new Size(96, 28) };

    public FindReplaceForm(MainForm main)
    {
        this.main = main;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(442, 152);
        Controls.AddRange(new Control[] { lblFind, lblReplace, txtFind, txtReplace, chkCase, chkWrap, rbUp, rbDown, btnNext, btnReplace, btnAll, btnClose });
        AcceptButton = btnNext;
        CancelButton = btnClose;

        btnNext.Click += (s, e) => main.FindNext(txtFind.Text, chkCase.Checked, rbUp.Checked, chkWrap.Checked);
        btnReplace.Click += (s, e) => main.ReplaceOne(txtFind.Text, txtReplace.Text, chkCase.Checked, rbUp.Checked, chkWrap.Checked);
        btnAll.Click += (s, e) => main.ReplaceAll(txtFind.Text, txtReplace.Text, chkCase.Checked);
        btnClose.Click += (s, e) => Hide();
        txtFind.TextChanged += (s, e) => UpdateButtons();
        UpdateButtons();
    }

    void UpdateButtons() => btnNext.Enabled = btnReplace.Enabled = btnAll.Enabled = txtFind.TextLength > 0;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    public void ShowFor(bool replace, string seed)
    {
        if (seed.Length > 0 && !seed.Contains('\n')) txtFind.Text = seed;
        Text = replace ? "Replace" : "Find";
        lblReplace.Visible = txtReplace.Visible = btnReplace.Visible = btnAll.Visible = replace;
        if (!Visible)
        {
            var o = main.Bounds;
            Location = new Point(Math.Max(o.Left, o.Right - Width - 40), o.Top + 110);
            Show(main);
        }
        Activate();
        txtFind.Focus();
        txtFind.SelectAll();
    }

    public void ApplyTheme(Palette p)
    {
        BackColor = p.Back;
        ForeColor = p.Text;
        foreach (var tb in new[] { txtFind, txtReplace }) { tb.BackColor = p.Surface; tb.ForeColor = p.Text; }
        foreach (var b in new[] { btnNext, btnReplace, btnAll, btnClose }) Theming.StyleButton(b, p);
        if (IsHandleCreated) Native.DarkTitleBar(this, p.Dark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(this, BackColor.GetBrightness() < 0.5f);
    }
}

static class Theming
{
    public static void StyleButton(Button b, Palette p)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = p.MenuBack;
        b.ForeColor = p.Text;
        b.FlatAppearance.BorderColor = p.Border;
        b.FlatAppearance.MouseOverBackColor = p.Hover;
        b.FlatAppearance.MouseDownBackColor = p.Border;
    }
}
