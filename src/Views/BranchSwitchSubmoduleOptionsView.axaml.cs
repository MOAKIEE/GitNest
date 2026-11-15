using Avalonia;
using Avalonia.Controls;

namespace SourceGit.Views
{
    public partial class BranchSwitchSubmoduleOptionsView : UserControl
    {
        public static readonly StyledProperty<double> LabelWidthProperty =
            AvaloniaProperty.Register<BranchSwitchSubmoduleOptionsView, double>(nameof(LabelWidth), 140);

        public double LabelWidth
        {
            get => GetValue(LabelWidthProperty);
            set => SetValue(LabelWidthProperty, value);
        }

        public BranchSwitchSubmoduleOptionsView()
        {
            InitializeComponent();
        }
    }
}
