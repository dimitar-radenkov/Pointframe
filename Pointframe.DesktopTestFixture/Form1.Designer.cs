namespace Pointframe.DesktopTestFixture;

partial class Form1
{
    private Button _saveButton = null!;
    private Button _closeButton = null!;
    private CheckBox _checkBox = null!;
    private TextBox _textBox = null!;
    private Panel _visualOnlyPanel = null!;

    private void InitializeComponent()
    {
        _saveButton = new Button();
        _closeButton = new Button();
        _checkBox = new CheckBox();
        _textBox = new TextBox();
        _visualOnlyPanel = new Panel();
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
        _visualOnlyPanel.Location = new Point(400, 120);
        _visualOnlyPanel.Name = "visualOnlyPanel";
        _visualOnlyPanel.Size = new Size(260, 180);
        _visualOnlyPanel.TabStop = false;

        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(720, 360);
        Controls.Add(_saveButton);
        Controls.Add(_closeButton);
        Controls.Add(_checkBox);
        Controls.Add(_textBox);
        Controls.Add(_visualOnlyPanel);
        Name = "Form1";
        Text = "Pointframe Desktop Test Fixture";
        ResumeLayout(false);
        PerformLayout();
    }
}
