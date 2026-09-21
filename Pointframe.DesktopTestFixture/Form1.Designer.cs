namespace Pointframe.DesktopTestFixture;

partial class Form1
{
    private Button _saveButton = null!;
    private Button _closeButton = null!;
    private CheckBox _checkBox = null!;
    private TextBox _textBox = null!;
    private Panel _visualOnlyPanel = null!;
    private Panel _clickTarget = null!;
    private Panel _dragSurface = null!;
    private Panel _scrollSurface = null!;
    private Label _statusLabel = null!;

    private void InitializeComponent()
    {
        _saveButton = new Button();
        _closeButton = new Button();
        _checkBox = new CheckBox();
        _textBox = new TextBox();
        _visualOnlyPanel = new Panel();
        _clickTarget = new Panel();
        _dragSurface = new Panel();
        _scrollSurface = new Panel();
        _statusLabel = new Label();
        SuspendLayout();

        _saveButton.Location = new Point(24, 120);
        _saveButton.Name = "saveButton";
        _saveButton.Size = new Size(110, 32);
        _saveButton.Text = "Save";
        _saveButton.UseVisualStyleBackColor = true;

        _closeButton.Location = new Point(144, 120);
        _closeButton.Name = "closeButton";
        _closeButton.Size = new Size(110, 32);
        _closeButton.Text = "Close";
        _closeButton.UseVisualStyleBackColor = true;
        _closeButton.Click += (_, _) => Close();

        _checkBox.AutoSize = true;
        _checkBox.Location = new Point(24, 172);
        _checkBox.Name = "checkBox";
        _checkBox.Text = "External fixture checkbox";

        _textBox.Location = new Point(24, 216);
        _textBox.Name = "textBox";
        _textBox.Size = new Size(320, 23);
        _textBox.PlaceholderText = "Unicode text input";

        _visualOnlyPanel.BackColor = Color.Transparent;
        _visualOnlyPanel.Location = new Point(24, 256);
        _visualOnlyPanel.Name = "visualOnlyPanel";
        _visualOnlyPanel.Size = new Size(260, 60);
        _visualOnlyPanel.TabStop = false;

        // Saturated, unique fill colours: the verification harness locates each surface by scanning
        // the downscaled preview PNG the observation hands back, so the colour is the contract.
        _clickTarget.BackColor = Color.FromArgb(255, 0, 255);
        _clickTarget.Location = new Point(460, 24);
        _clickTarget.Name = "clickTarget";
        _clickTarget.Size = new Size(400, 140);

        _dragSurface.BackColor = Color.FromArgb(0, 255, 0);
        _dragSurface.Location = new Point(460, 184);
        _dragSurface.Name = "dragSurface";
        _dragSurface.Size = new Size(400, 140);

        _scrollSurface.BackColor = Color.FromArgb(0, 255, 255);
        _scrollSurface.Location = new Point(460, 344);
        _scrollSurface.Name = "scrollSurface";
        _scrollSurface.Size = new Size(400, 140);

        _statusLabel.AutoSize = false;
        _statusLabel.BackColor = Color.White;
        _statusLabel.Location = new Point(24, 330);
        _statusLabel.Name = "statusLabel";
        _statusLabel.Size = new Size(410, 150);
        _statusLabel.Text = "ready";

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(900, 510);
        Controls.Add(_saveButton);
        Controls.Add(_closeButton);
        Controls.Add(_checkBox);
        Controls.Add(_textBox);
        Controls.Add(_visualOnlyPanel);
        Controls.Add(_clickTarget);
        Controls.Add(_dragSurface);
        Controls.Add(_scrollSurface);
        Controls.Add(_statusLabel);
        Name = "Form1";
        Text = "Pointframe Desktop Test Fixture";
        ResumeLayout(false);
        PerformLayout();
    }
}
