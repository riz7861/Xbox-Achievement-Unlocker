using Wpf.Ui.Controls;
using XAU.ViewModels.Pages;

namespace XAU.Views.Pages;

public partial class QuantumBreakResearchPage : INavigableView<QuantumBreakResearchViewModel>
{
    public QuantumBreakResearchViewModel ViewModel { get; }

    public QuantumBreakResearchPage(QuantumBreakResearchViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
