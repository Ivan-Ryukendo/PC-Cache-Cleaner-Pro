using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CleanPC.Pro
{
    /// <summary>MVVM-light base: INotifyPropertyChanged helper.</summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return false;
            field = value; Raise(name); return true;
        }
    }

    /// <summary>MVVM-light ICommand.</summary>
    public class RelayCommand : ICommand
    {
        readonly Action<object> _run; readonly Func<object, bool> _can;
        public RelayCommand(Action run, Func<bool> can = null) : this(o => run(), can == null ? (Func<object, bool>)null : (o => can())) { }
        public RelayCommand(Action<object> run, Func<object, bool> can = null) { _run = run; _can = can; }
        public bool CanExecute(object p) { return _can == null || _can(p); }
        public void Execute(object p)
        {
            try { _run(p); }
            catch (Exception e) { Log.Error("command", e); App_ShowError(e); }
        }
        public event EventHandler CanExecuteChanged { add { CommandManager.RequerySuggested += value; } remove { CommandManager.RequerySuggested -= value; } }
        public static Action<Exception> ShowErrorHook;
        static void App_ShowError(Exception e) { var h = ShowErrorHook; if (h != null) h(e); }
    }
}
