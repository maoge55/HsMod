using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HsModManager.Models;

namespace HsModManager.Controls;

public partial class SearchableSkinComboBox : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(SearchableSkinComboBox),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedIdProperty = DependencyProperty.Register(
        nameof(SelectedId),
        typeof(int),
        typeof(SearchableSkinComboBox),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIdChanged));

    private List<SkinItem> _allItems = [];
    private bool _isUpdatingSelection;

    public SearchableSkinComboBox()
    {
        InitializeComponent();
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int SelectedId
    {
        get => (int)GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (SearchableSkinComboBox)d;
        control._allItems = (e.NewValue as IEnumerable)?.OfType<SkinItem>().ToList() ?? [];
        control.ApplyFilter();
        control.UpdateSelectedItem();
    }

    private static void OnSelectedIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SearchableSkinComboBox)d).UpdateSelectedItem();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (OptionsList == null || SearchTextBox == null)
        {
            return;
        }

        string keyword = SearchTextBox.Text.Trim();
        List<SkinItem> filtered = string.IsNullOrWhiteSpace(keyword)
            ? _allItems
            : _allItems
                .Where(item => item.Display.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        _isUpdatingSelection = true;
        OptionsList.ItemsSource = filtered;
        OptionsList.SelectedItem = filtered.FirstOrDefault(item => item.Id == SelectedId);
        _isUpdatingSelection = false;
    }

    private void UpdateSelectedItem()
    {
        if (SelectedTextBlock == null || OptionsList == null)
        {
            return;
        }

        SkinItem? selected = _allItems.FirstOrDefault(item => item.Id == SelectedId);
        SelectedTextBlock.Text = selected?.Display ?? "请选择";

        _isUpdatingSelection = true;
        OptionsList.SelectedItem = OptionsList.Items.OfType<SkinItem>().FirstOrDefault(item => item.Id == SelectedId);
        _isUpdatingSelection = false;
    }

    private void OptionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection || DropDownPopup.IsOpen != true || OptionsList.SelectedItem is not SkinItem selected)
        {
            return;
        }

        SetCurrentValue(SelectedIdProperty, selected.Id);
        DropDownToggle.IsChecked = false;
    }

    private void OptionsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OptionsList.SelectedItem is SkinItem selected)
        {
            SetCurrentValue(SelectedIdProperty, selected.Id);
            DropDownToggle.IsChecked = false;
            e.Handled = true;
        }
    }

    private void DropDownPopup_Opened(object? sender, EventArgs e)
    {
        SearchTextBox.Clear();
        ApplyFilter();
        Dispatcher.BeginInvoke(() =>
        {
            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            if (OptionsList.SelectedItem != null)
            {
                OptionsList.ScrollIntoView(OptionsList.SelectedItem);
            }
        }, DispatcherPriority.Input);
    }
}
