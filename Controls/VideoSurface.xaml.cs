using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StretchCordNG.Services;

namespace StretchCordNG.Controls
{
    /// <summary>
    /// Shows the presented D3DImage stretched to fill, with the pointer composited on top at
    /// the exact position it occupies in the source window. Used for both the main-window
    /// preview and the stream window Discord captures.
    ///
    /// The frame source is the presenter's D3DImage — every surface here displays the same
    /// GPU texture, so two windows showing the stream cost nothing beyond composition.
    ///
    /// The cursor overlay follows the render loop through <see cref="CompositionTarget.Rendering"/>
    /// instead of a fixed timer, so it costs nothing when the video is not playing.
    /// </summary>
    public partial class VideoSurface : UserControl
    {
        private IntPtr _sourceHwnd;
        private int _frameWidth;
        private int _frameHeight;
        private bool _showCursor = true;
        private Stretch _stretch = Stretch.Fill;
        private bool _renderHooked;

        public VideoSurface()
        {
            InitializeComponent();

            Loaded += (_, _) => HookRendering();
            Unloaded += (_, _) => UnhookRendering();
            IsVisibleChanged += (_, _) => HookRendering();
        }

        private void HookRendering()
        {
            if (_renderHooked || !IsLoaded || !IsVisible) return;
            CompositionTarget.Rendering += OnRenderTick;
            _renderHooked = true;
        }

        private void UnhookRendering()
        {
            if (!_renderHooked) return;
            CompositionTarget.Rendering -= OnRenderTick;
            _renderHooked = false;
        }

        public void SetSourceWindow(IntPtr hwnd) => _sourceHwnd = hwnd;

        public void SetShowCursor(bool show)
        {
            _showCursor = show;
            if (!show) HideCursor();
        }

        public void SetStretch(bool fill)
        {
            _stretch = fill ? Stretch.Fill : Stretch.Uniform;
            FrameImage.Stretch = _stretch;
            UpdateCursor();
        }

        /// <summary>Called for every presented frame (UI thread).</summary>
        public void ShowFrame(System.Windows.Interop.D3DImage image)
        {
            if (FrameImage.Source != image)
                FrameImage.Source = image;

            _frameWidth = image.PixelWidth;
            _frameHeight = image.PixelHeight;

            FrameImage.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            UpdateCursor();
        }

        public void ShowEmpty(string title, string subtitle)
        {
            FrameImage.Source = null;
            FrameImage.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = title;
            EmptySubtitle.Text = subtitle;
            HideCursor();
        }

        private void OnRenderTick(object? sender, EventArgs e) => UpdateCursor();

        private void UpdateCursor()
        {
            if (!_showCursor) { HideCursor(); return; }
            if (_sourceHwnd == IntPtr.Zero) { HideCursor(); return; }
            if (_frameWidth <= 0 || _frameHeight <= 0) { HideCursor(); return; }
            if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0) { HideCursor(); return; }

            var placement = CursorOverlayService.PlaceInFrame(_sourceHwnd, _frameWidth, _frameHeight);
            if (!placement.Visible)
            {
                HideCursor();
                return;
            }

            var image = CursorOverlayService.CurrentCursorImage();
            if (image == null)
            {
                HideCursor();
                return;
            }

            // With Uniform (letterboxed) scaling the picture does not cover the whole
            // element, so the mapping has to use the rendered rect, not the element rect.
            double areaWidth = Root.ActualWidth;
            double areaHeight = Root.ActualHeight;
            double frameAspect = (double)_frameWidth / _frameHeight;
            double areaAspect = areaWidth / areaHeight;

            double offsetX = 0, offsetY = 0;
            if (_stretch == Stretch.Uniform)
            {
                if (frameAspect > areaAspect)
                {
                    double rendered = areaWidth / frameAspect;
                    offsetY = (areaHeight - rendered) / 2;
                    areaHeight = rendered;
                }
                else
                {
                    double rendered = areaHeight * frameAspect;
                    offsetX = (areaWidth - rendered) / 2;
                    areaWidth = rendered;
                }
            }

            var rect = CursorOverlayService.ToElement(placement, _frameWidth, _frameHeight, areaWidth, areaHeight);

            if (CursorImage.Source != image)
                CursorImage.Source = image;

            CursorImage.Width = rect.Width;
            CursorImage.Height = rect.Height;
            Canvas.SetLeft(CursorImage, rect.X + offsetX);
            Canvas.SetTop(CursorImage, rect.Y + offsetY);
            CursorImage.Visibility = Visibility.Visible;
        }

        private void HideCursor()
        {
            CursorImage.Visibility = Visibility.Collapsed;
        }
    }
}
