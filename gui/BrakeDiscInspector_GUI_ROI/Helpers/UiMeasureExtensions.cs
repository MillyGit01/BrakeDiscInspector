using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace BrakeDiscInspector_GUI_ROI.Helpers
{
    public static class UiMeasureExtensions
    {
        // CODEX: wait once until ActualWidth/Height are non-zero and a layout/render pass happened
        public static Task WaitUntilMeasuredAsync(this FrameworkElement fe, CancellationToken ct)
        {
            if (fe == null) throw new ArgumentNullException(nameof(fe));
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Done()
            {
                fe.Loaded -= OnLoaded;
                fe.LayoutUpdated -= OnLayout;
                CompositionTarget.Rendering -= OnRendering;
                if (!tcs.Task.IsCompleted) tcs.TrySetResult(null);
            }
            void Check()
            {
                if (fe.ActualWidth > 0 && fe.ActualHeight > 0)
                {
                    // One more render ensures transform is valid
                    CompositionTarget.Rendering += OnRendering;
                }
            }
            void OnLoaded(object? s, RoutedEventArgs e) => Check();
            void OnLayout(object? s, EventArgs e) => Check();
            void OnRendering(object? s, EventArgs e) => Done();

            ct.Register(() => { if (!tcs.Task.IsCompleted) tcs.TrySetCanceled(ct); });
            fe.Loaded += OnLoaded;
            fe.LayoutUpdated += OnLayout;
            Check();
            return tcs.Task;
        }
    }
}
