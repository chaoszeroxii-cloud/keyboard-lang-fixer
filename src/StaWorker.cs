// A resident STA with its own message pump. Reusing it avoids thread startup
// on every hotkey and keeps COM/clipboard objects in a live apartment.
using System;
using System.Threading;
using System.Windows.Forms;

namespace KbFix
{
    internal sealed class StaWorker : IDisposable
    {
        private readonly Thread _thread;
        private Control _dispatcher;
        private volatile bool _stopping;

        public StaWorker()
        {
            Exception failure = null;
            using (ManualResetEvent ready = new ManualResetEvent(false))
            {
                _thread = new Thread(delegate()
                {
                    try
                    {
                        // Force handle creation on this STA, so BeginInvoke
                        // can safely queue work even before Run starts.
                        _dispatcher = new Control();
                        IntPtr handle = _dispatcher.Handle;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        if (_dispatcher != null) _dispatcher.Dispose();
                        ready.Set();
                        return;
                    }
                    ready.Set();
                    using (_dispatcher) Application.Run();
                });
                _thread.IsBackground = true;
                _thread.Name = "KbFix worker";
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.WaitOne();
            }
            if (failure != null) throw new InvalidOperationException("Could not start the fix worker", failure);
        }

        public void Invoke(Action action)
        {
            if (_stopping) throw new ObjectDisposedException("StaWorker");
            _dispatcher.Invoke(action);
        }

        public void Post(Action action)
        {
            if (_stopping) return;
            _dispatcher.BeginInvoke(new Action(delegate { if (!_stopping) action(); }));
        }

        public void Dispose()
        {
            if (_stopping) return;
            _stopping = true;
            // Never Join on the hook/UI thread: an in-flight COM call can need
            // that thread to pump messages. Exit after the current job returns.
            _dispatcher.BeginInvoke(new Action(Application.ExitThread));
        }
    }
}
