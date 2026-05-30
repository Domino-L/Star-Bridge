using System.Drawing.Drawing2D;

namespace SCFleetCommand.App;

internal sealed class AvatarCropDialog : Form
{
    private readonly Image _sourceImage;
    private readonly Panel _canvas = new();
    private Rectangle _cropRect;
    private bool _dragging;
    private Point _dragOffset;

    public Bitmap? CroppedAvatar { get; private set; }

    public AvatarCropDialog(string imagePath)
    {
        _sourceImage = Image.FromFile(imagePath);

        Text = "Crop Avatar";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 640);
        BackColor = Color.FromArgb(5, 10, 17);
        ForeColor = Color.FromArgb(234, 244, 255);

        BuildLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sourceImage.Dispose();
            CroppedAvatar?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16),
            BackColor = BackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _canvas.Dock = DockStyle.Fill;
        _canvas.BackColor = Color.FromArgb(10, 17, 28);
        _canvas.Paint += PaintCanvas;
        _canvas.MouseDown += CanvasMouseDown;
        _canvas.MouseMove += CanvasMouseMove;
        _canvas.MouseUp += (_, _) => _dragging = false;
        _canvas.Resize += (_, _) => ResetCropRect();

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = BackColor
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Drag the square crop frame. Only the framed area will be shown.",
            ForeColor = Color.FromArgb(151, 181, 213),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var save = new Button { Dock = DockStyle.Fill, Text = "Use Crop" };
        StyleButton(save, true);
        save.Click += (_, _) => SaveCrop();

        var cancel = new Button { Dock = DockStyle.Fill, Text = "Cancel" };
        StyleButton(cancel, false);
        cancel.Click += (_, _) => Close();

        buttons.Controls.Add(hint, 0, 0);
        buttons.Controls.Add(save, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);

        root.Controls.Add(_canvas, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        ResetCropRect();
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(61, 126, 184);
        button.BackColor = primary ? Color.FromArgb(88, 190, 255) : Color.FromArgb(16, 27, 43);
        button.ForeColor = primary ? Color.White : Color.FromArgb(234, 244, 255);
        button.Font = new Font("Segoe UI Semibold", 9.5F);
    }

    private void ResetCropRect()
    {
        if (_canvas.Width <= 0 || _canvas.Height <= 0)
        {
            return;
        }

        var imageRect = GetImageDisplayRect();
        var size = Math.Min(imageRect.Width, imageRect.Height);
        size = Math.Max(80, (int)(size * 0.72));
        _cropRect = new Rectangle(
            imageRect.X + (imageRect.Width - size) / 2,
            imageRect.Y + (imageRect.Height - size) / 2,
            size,
            size);
        _canvas.Invalidate();
    }

    private void PaintCanvas(object? sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var imageRect = GetImageDisplayRect();
        e.Graphics.DrawImage(_sourceImage, imageRect);

        using var shade = new SolidBrush(Color.FromArgb(135, 0, 0, 0));
        using var region = new Region(_canvas.ClientRectangle);
        region.Exclude(_cropRect);
        e.Graphics.FillRegion(shade, region);

        using var border = new Pen(Color.FromArgb(107, 223, 255), 2F);
        using var inner = new Pen(Color.FromArgb(170, 255, 255, 255), 1F);
        e.Graphics.DrawRectangle(border, _cropRect);
        e.Graphics.DrawRectangle(inner, Rectangle.Inflate(_cropRect, -4, -4));
    }

    private void CanvasMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_cropRect.Contains(e.Location))
        {
            return;
        }

        _dragging = true;
        _dragOffset = new Point(e.X - _cropRect.X, e.Y - _cropRect.Y);
    }

    private void CanvasMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var imageRect = GetImageDisplayRect();
        var x = Math.Clamp(e.X - _dragOffset.X, imageRect.Left, imageRect.Right - _cropRect.Width);
        var y = Math.Clamp(e.Y - _dragOffset.Y, imageRect.Top, imageRect.Bottom - _cropRect.Height);
        _cropRect.Location = new Point(x, y);
        _canvas.Invalidate();
    }

    private void SaveCrop()
    {
        var imageRect = GetImageDisplayRect();
        var scaleX = (double)_sourceImage.Width / imageRect.Width;
        var scaleY = (double)_sourceImage.Height / imageRect.Height;

        var sourceRect = new Rectangle(
            (int)((_cropRect.X - imageRect.X) * scaleX),
            (int)((_cropRect.Y - imageRect.Y) * scaleY),
            (int)(_cropRect.Width * scaleX),
            (int)(_cropRect.Height * scaleY));

        sourceRect.Intersect(new Rectangle(0, 0, _sourceImage.Width, _sourceImage.Height));

        CroppedAvatar?.Dispose();
        CroppedAvatar = new Bitmap(256, 256);
        using var graphics = Graphics.FromImage(CroppedAvatar);
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(_sourceImage, new Rectangle(0, 0, 256, 256), sourceRect, GraphicsUnit.Pixel);

        DialogResult = DialogResult.OK;
        Close();
    }

    private Rectangle GetImageDisplayRect()
    {
        var available = _canvas.ClientRectangle;
        available.Inflate(-16, -16);

        var imageRatio = (double)_sourceImage.Width / _sourceImage.Height;
        var areaRatio = (double)available.Width / available.Height;

        if (imageRatio > areaRatio)
        {
            var height = (int)(available.Width / imageRatio);
            return new Rectangle(available.X, available.Y + (available.Height - height) / 2, available.Width, height);
        }

        var width = (int)(available.Height * imageRatio);
        return new Rectangle(available.X + (available.Width - width) / 2, available.Y, width, available.Height);
    }
}
