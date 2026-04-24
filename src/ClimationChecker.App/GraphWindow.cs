using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace ClimationChecker.App;

internal sealed class GraphWindow : Window
{
    private const int MaxSurfaceDimension = 150;
    private const double SurfaceXyScale = 2.2;
    private const double SurfaceZScale = 120.0;

    private readonly Border _viewportHost;
    private readonly Viewport3D _viewport;
    private readonly PerspectiveCamera _camera;
    private readonly AxisAngleRotation3D _rotationX;
    private readonly AxisAngleRotation3D _rotationZ;
    private readonly GeometryModel3D _surfaceModel;
    private readonly Model3DGroup _axisGroup;
    private readonly Transform3DGroup _graphTransform;
    private readonly Model3DGroup _plotGroup;

    private bool _isRotating;
    private Point _rotationStartPoint;
    private double _rotationStartX;
    private double _rotationStartZ;
    private double _cameraDistance = 340;

    public GraphWindow()
    {
        Title = "DonutScope Graph";
        Width = 980;
        Height = 760;
        MinWidth = 250;
        MinHeight = 250;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0E141A"));
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app-icon.ico"));
        SourceInitialized += (_, _) => WindowTheme.ApplyDarkTitleBar(this);

        var root = new Grid { Margin = new Thickness(16) };

        _viewportHost = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#05090D")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#263442")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
        };

        _viewport = new Viewport3D
        {
            ClipToBounds = true,
        };
        _viewport.MouseLeftButtonDown += Viewport_OnMouseLeftButtonDown;
        _viewport.MouseLeftButtonUp += Viewport_OnMouseLeftButtonUp;
        _viewport.MouseMove += Viewport_OnMouseMove;
        _viewport.MouseWheel += Viewport_OnMouseWheel;
        _viewport.MouseLeave += Viewport_OnMouseLeave;
        _viewportHost.Child = _viewport;
        root.Children.Add(_viewportHost);

        _camera = new PerspectiveCamera
        {
            FieldOfView = 48,
            UpDirection = new Vector3D(0, 0, 1),
        };
        UpdateCamera();
        _viewport.Camera = _camera;

        _rotationX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 20);
        _rotationZ = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        _graphTransform = new Transform3DGroup
        {
            Children =
            {
                new RotateTransform3D(_rotationX),
                new RotateTransform3D(_rotationZ),
            },
        };

        var modelGroup = new Model3DGroup();
        modelGroup.Children.Add(new AmbientLight((Color)ColorConverter.ConvertFromString("#747E88")));
        modelGroup.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-0.4, -0.6, -1.0)));
        modelGroup.Children.Add(new DirectionalLight((Color)ColorConverter.ConvertFromString("#7FD6FF"), new Vector3D(0.6, 0.2, -0.7)));

        _plotGroup = new Model3DGroup
        {
            Transform = _graphTransform,
        };

        _surfaceModel = new GeometryModel3D();
        _plotGroup.Children.Add(_surfaceModel);
        _axisGroup = new Model3DGroup();
        _plotGroup.Children.Add(_axisGroup);
        modelGroup.Children.Add(_plotGroup);

        var visual = new ModelVisual3D { Content = modelGroup };
        _viewport.Children.Add(visual);
        Content = root;
    }

    public void SetStatus(string text, Brush? foreground = null)
    {
        _ = text;
        _ = foreground;
    }

    public void UpdateSurface(float[] sourceData, int width, int height)
    {
        var reduced = Downsample(sourceData, width, height, out var reducedWidth, out var reducedHeight);
        if (reduced.Length == 0)
        {
            SetStatus("No graph data is available for the current frame.", Brushes.OrangeRed);
            return;
        }

        var mesh = BuildSurfaceMesh(reduced, reducedWidth, reducedHeight, out var texture);
        var material = new DiffuseMaterial(new ImageBrush(texture) { Stretch = Stretch.Fill });
        _surfaceModel.Geometry = mesh;
        _surfaceModel.Material = material;
        _surfaceModel.BackMaterial = material;
        UpdateAxes(reducedWidth, reducedHeight);
        SetStatus("Interactive donut intensity graph is up to date.");
    }

    private static float[] Downsample(float[] sourceData, int width, int height, out int reducedWidth, out int reducedHeight)
    {
        var stride = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)MaxSurfaceDimension));
        reducedWidth = (width + stride - 1) / stride;
        reducedHeight = (height + stride - 1) / stride;

        var reduced = new float[reducedWidth * reducedHeight];
        var targetIndex = 0;
        for (var y = 0; y < height; y += stride)
        {
            for (var x = 0; x < width; x += stride)
            {
                reduced[targetIndex++] = sourceData[(y * width) + x];
            }
        }

        return reduced;
    }

    private static MeshGeometry3D BuildSurfaceMesh(float[] data, int width, int height, out BitmapSource heatmap)
    {
        var min = data.Min();
        var max = data.Max();
        var range = Math.Max(max - min, 1e-6f);

        var mesh = new MeshGeometry3D();
        var pixels = new byte[width * height * 4];
        var xCenter = (width - 1) / 2.0;
        var yCenter = (height - 1) / 2.0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                var normalized = (data[index] - min) / range;
                var px = (x - xCenter) * SurfaceXyScale;
                var py = (yCenter - y) * SurfaceXyScale;
                var pz = normalized * SurfaceZScale;
                mesh.Positions.Add(new Point3D(px, py, pz));
                mesh.TextureCoordinates.Add(new Point((double)x / Math.Max(width - 1, 1), (double)y / Math.Max(height - 1, 1)));

                var color = InfernoColor(normalized);
                var pixelOffset = index * 4;
                pixels[pixelOffset] = color.B;
                pixels[pixelOffset + 1] = color.G;
                pixels[pixelOffset + 2] = color.R;
                pixels[pixelOffset + 3] = color.A;
            }
        }

        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width - 1; x++)
            {
                var topLeft = (y * width) + x;
                var topRight = topLeft + 1;
                var bottomLeft = topLeft + width;
                var bottomRight = bottomLeft + 1;

                mesh.TriangleIndices.Add(topLeft);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(topRight);

                mesh.TriangleIndices.Add(topRight);
                mesh.TriangleIndices.Add(bottomLeft);
                mesh.TriangleIndices.Add(bottomRight);
            }
        }

        heatmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        heatmap.Freeze();
        return mesh;
    }

    private static Color InfernoColor(double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        var stops = new[]
        {
            (0.0, Color.FromRgb(0x14, 0x0A, 0x3A)),
            (0.18, Color.FromRgb(0x4D, 0x14, 0x8C)),
            (0.38, Color.FromRgb(0x9D, 0x2D, 0xC5)),
            (0.58, Color.FromRgb(0xFF, 0x4F, 0x87)),
            (0.78, Color.FromRgb(0xFF, 0x92, 0x1C)),
            (0.92, Color.FromRgb(0xFF, 0xD8, 0x2A)),
            (1.0, Color.FromRgb(0xFF, 0xFF, 0xB2)),
        };

        for (var index = 0; index < stops.Length - 1; index++)
        {
            var (startPos, startColor) = stops[index];
            var (endPos, endColor) = stops[index + 1];
            if (value <= endPos)
            {
                var t = (value - startPos) / (endPos - startPos);
                return Color.FromArgb(
                    255,
                    (byte)Math.Round(startColor.R + ((endColor.R - startColor.R) * t)),
                    (byte)Math.Round(startColor.G + ((endColor.G - startColor.G) * t)),
                    (byte)Math.Round(startColor.B + ((endColor.B - startColor.B) * t)));
            }
        }

        return stops[^1].Item2;
    }

    private void UpdateAxes(int width, int height)
    {
        _axisGroup.Children.Clear();

        var xLength = Math.Max((width - 1) * SurfaceXyScale, 160);
        var yLength = Math.Max((height - 1) * SurfaceXyScale, 160);
        var zLength = 150.0;
        var axisRadius = 0.8;
        var xOrigin = -((width - 1) / 2.0) * SurfaceXyScale;
        var yOrigin = -((height - 1) / 2.0) * SurfaceXyScale;
        var origin = new Point3D(xOrigin, yOrigin, 0);
        var zOrigin = new Point3D(origin.X, origin.Y + yLength, 0);

        _axisGroup.Children.Add(CreateAxisModel(
            origin,
            new Point3D(origin.X + xLength, origin.Y, 0),
            axisRadius,
            Color.FromRgb(0x4D, 0xC7, 0xFF)));
        _axisGroup.Children.Add(CreateAxisModel(
            origin,
            new Point3D(origin.X, origin.Y + yLength, 0),
            axisRadius,
            Color.FromRgb(0x7A, 0xFF, 0xB2)));
        _axisGroup.Children.Add(CreateAxisModel(
            zOrigin,
            new Point3D(zOrigin.X, zOrigin.Y, zLength),
            axisRadius,
            Color.FromRgb(0xFF, 0xD2, 0x4A)));

        _axisGroup.Children.Add(CreateAxisLabel("X", new Point3D(origin.X + xLength + 12, origin.Y, 0), Color.FromRgb(0x4D, 0xC7, 0xFF)));
        _axisGroup.Children.Add(CreateAxisLabel("Y", new Point3D(origin.X, origin.Y + yLength + 12, 0), Color.FromRgb(0x7A, 0xFF, 0xB2)));
        _axisGroup.Children.Add(CreateAxisLabel("Z", new Point3D(zOrigin.X, zOrigin.Y, zLength + 12), Color.FromRgb(0xFF, 0xD2, 0x4A)));
    }

    private static GeometryModel3D CreateAxisModel(Point3D start, Point3D end, double radius, Color color)
    {
        var direction = end - start;
        var length = direction.Length;
        direction.Normalize();

        var segments = 18;
        var mesh = new MeshGeometry3D();
        var perpendicular = Vector3D.CrossProduct(direction, new Vector3D(0, 0, 1));
        if (perpendicular.Length < 0.001)
        {
            perpendicular = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
        }

        perpendicular.Normalize();
        var bitangent = Vector3D.CrossProduct(direction, perpendicular);
        bitangent.Normalize();

        for (var i = 0; i <= segments; i++)
        {
            var angle = (Math.PI * 2.0 * i) / segments;
            var radial = (Math.Cos(angle) * perpendicular) + (Math.Sin(angle) * bitangent);
            var offset = radial * radius;
            mesh.Positions.Add(start + offset);
            mesh.Positions.Add(end + offset);
        }

        for (var i = 0; i < segments; i++)
        {
            var baseIndex = i * 2;
            mesh.TriangleIndices.Add(baseIndex);
            mesh.TriangleIndices.Add(baseIndex + 1);
            mesh.TriangleIndices.Add(baseIndex + 2);

            mesh.TriangleIndices.Add(baseIndex + 1);
            mesh.TriangleIndices.Add(baseIndex + 3);
            mesh.TriangleIndices.Add(baseIndex + 2);
        }

        var material = new DiffuseMaterial(new SolidColorBrush(color));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static Model3D CreateAxisLabel(string axisName, Point3D position, Color color)
    {
        var text = new TextBlock
        {
            Text = axisName,
            Foreground = new SolidColorBrush(color),
            Background = Brushes.Transparent,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
        };

        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        text.Arrange(new Rect(text.DesiredSize));

        var brush = new VisualBrush(text);
        var material = new DiffuseMaterial(brush);
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(position.X, position.Y, position.Z),
                new(position.X + 12, position.Y, position.Z),
                new(position.X + 12, position.Y, position.Z + 12),
                new(position.X, position.Y, position.Z + 12),
            },
            TextureCoordinates = new PointCollection
            {
                new(0, 1),
                new(1, 1),
                new(1, 0),
                new(0, 0),
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };

        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private void Viewport_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isRotating = true;
        _rotationStartPoint = e.GetPosition(_viewport);
        _rotationStartX = _rotationX.Angle;
        _rotationStartZ = _rotationZ.Angle;
        _viewport.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void Viewport_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndRotation();
    }

    private void Viewport_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndRotation();
        }
    }

    private void Viewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRotating || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(_viewport);
        var deltaX = currentPoint.X - _rotationStartPoint.X;
        var deltaY = currentPoint.Y - _rotationStartPoint.Y;
        _rotationZ.Angle = _rotationStartZ + (deltaX * 0.35);
        _rotationX.Angle = Math.Clamp(_rotationStartX + (deltaY * 0.35), 10, 88);
    }

    private void Viewport_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _cameraDistance = Math.Clamp(_cameraDistance - (e.Delta * 0.08), 120, 900);
        UpdateCamera();
        e.Handled = true;
    }

    private void UpdateCamera()
    {
        _camera.Position = new Point3D(0, -_cameraDistance, _cameraDistance * 0.72);
        _camera.LookDirection = new Vector3D(0, _cameraDistance, -_camera.Position.Z * 0.92);
    }

    private void EndRotation()
    {
        if (!_isRotating)
        {
            return;
        }

        _isRotating = false;
        _viewport.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }
}
