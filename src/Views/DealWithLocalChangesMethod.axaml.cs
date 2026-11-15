using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class DealWithLocalChangesMethod : UserControl
    {
        public static readonly DirectProperty<DealWithLocalChangesMethod, Models.DealWithLocalChanges> MethodProperty =
            AvaloniaProperty.RegisterDirect<DealWithLocalChangesMethod, Models.DealWithLocalChanges>(
                nameof(Method),
                o => o.Method,
                (o, v) => o.Method = v,
                defaultBindingMode: BindingMode.TwoWay);

        public Models.DealWithLocalChanges Method
        {
            get => _method;
            set
            {
                var safeValue = value == Models.DealWithLocalChanges.Discard
                    ? Models.DealWithLocalChanges.DoNothing
                    : value;

                if (SetAndRaise(MethodProperty, ref _method, safeValue))
                    UpdateUI();
            }
        }

        public DealWithLocalChangesMethod()
        {
            InitializeComponent();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            UpdateUI();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Up)
            {
                if (Method == Models.DealWithLocalChanges.StashAndReapply)
                    Method = Models.DealWithLocalChanges.DoNothing;
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                if (Method == Models.DealWithLocalChanges.DoNothing)
                    Method = Models.DealWithLocalChanges.StashAndReapply;
                e.Handled = true;
            }
        }

        private void OnRadioButtonClicked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: Models.DealWithLocalChanges way })
            {
                Method = way;
                Focus();
            }
        }

        private void UpdateUI()
        {
            if (RadioDoNothing == null || RadioStashAndReapply == null)
                return;

            switch (_method)
            {
                case Models.DealWithLocalChanges.DoNothing:
                    RadioDoNothing.IsChecked = true;
                    break;
                default:
                    RadioStashAndReapply.IsChecked = true;
                    break;
            }
        }

        private Models.DealWithLocalChanges _method = Models.DealWithLocalChanges.DoNothing;
    }
}
