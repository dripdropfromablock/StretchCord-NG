using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StretchCordNG.Models;
using StretchCordNG.Services;

namespace StretchCordNG.UI
{
    public partial class WindowPickerDialog : Window
    {
        private List<WindowInfo> _allWindows = new();

        public WindowInfo? SelectedWindow { get; private set; }

        public WindowPickerDialog()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                RefreshList();
                SearchBox.Focus();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Services.NativeMethods.ApplyDarkTitleBar(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
        }

        private void RefreshList()
        {
            var selection = WindowList.SelectedItem as WindowInfo;
            _allWindows = WindowEnumerator.GetCapturableWindows();
            ApplyFilter(selection);
        }

        private void ApplyFilter(WindowInfo? keepSelected = null)
        {
            var filter = SearchBox.Text.Trim();
            IEnumerable<WindowInfo> query = _allWindows;

            if (filter.Length > 0)
            {
                query = query.Where(w =>
                    w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            var results = query.ToList();
            WindowList.ItemsSource = results;

            if (keepSelected != null)
                WindowList.SelectedItem = results.FirstOrDefault(w => w.Hwnd == keepSelected.Hwnd);

            if (WindowList.SelectedItem == null && results.Count == 1)
                WindowList.SelectedIndex = 0;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshList();

        private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = WindowList.SelectedItem as WindowInfo;
            BtnSelect.IsEnabled = selected != null;
            SelectionLabel.Text = selected?.ToString() ?? "Double-click a window, or select one and press Enter";
        }

        private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (WindowList.SelectedItem is WindowInfo) Confirm();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && BtnSelect.IsEnabled)
            {
                Confirm();
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                RefreshList();
                e.Handled = true;
            }
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e) => Confirm();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Confirm()
        {
            if (WindowList.SelectedItem is not WindowInfo window) return;

            SelectedWindow = window;
            DialogResult = true;
            Close();
        }
    }
}
