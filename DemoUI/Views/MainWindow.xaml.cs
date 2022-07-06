using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using DemoUI.ViewModels;
using MvsAppApi.Core;
using MvsAppApi.Core.Structs;

namespace DemoUI.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _viewModel;
        
        public MainWindow(IAdapter adapter)
        {
            InitializeComponent();

            DataContext = _viewModel = new MainWindowViewModel(adapter) { CloseAction = Close };
        }

        private void MainWindow_OnLoaded(object sender, EventArgs e)
        {
            _viewModel.Attach();
        }


        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!_viewModel.ShouldClose)
            {
                // cancel the close and hide the window instead
                e.Cancel = true;
                if (_viewModel.HideCommand.CanExecute(null))
                    _viewModel.HideCommand.Execute(null);
            }
        }

        // a bit of a workaround here to get access to SelectedItems
        // see: https://stackoverflow.com/questions/9880589/bind-to-selecteditems-from-datagrid-or-listbox-in-mvvm/16953833#16953833
        private void PlayersDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var newSelectedPlayers = new ObservableCollection<MainWindowViewModel.PlayerInfo>();
            foreach (var i in PlayersDataGrid.SelectedItems) 
                newSelectedPlayers.Add(i as MainWindowViewModel.PlayerInfo);
            _viewModel.SelectedPlayers = newSelectedPlayers;
        }

        // same with these ...

        private void StatsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var newSelectedStats = new ObservableCollection<StatInfo>();
            foreach (var i in StatsDataGrid.SelectedItems) 
                newSelectedStats.Add(i as StatInfo);
            _viewModel.SelectedStats = newSelectedStats;
        }

        private void PtsqlQueryResultsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var newSelectedItems = new Collection<DataRowView>();
            foreach (DataRowView i in PtsqlQueryResultsDataGrid.SelectedItems)
                newSelectedItems.Add(i);
            _viewModel.SelectedPtsqlQueryResultsRows = newSelectedItems;
        }

        private void PositionalStatsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var newSelectedItems = new Collection<DataRowView>();
            foreach (DataRowView i in PositionalStatsDataGrid.SelectedItems)
                newSelectedItems.Add(i);
            _viewModel.SelectedPtsqlQueryResultsRows = newSelectedItems;
        }

        private void HandsMenuOptions_OnDropDownClosed(object sender, EventArgs e)
        {
            _viewModel.RegisterHandsMenuCommand.Execute(null);
        }

        private void UseCustomHandsMenuIconCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            _viewModel.RegisterHandsMenuCommand.Execute(null);
        }

        private void HandsMenuHandFormat_OnDropDownClosed(object sender, EventArgs e)
        {
            _viewModel.RegisterHandsMenuCommand.Execute(null);
        }
    }
}
