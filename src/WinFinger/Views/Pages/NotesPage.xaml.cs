using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinFinger.Models;
using WinFinger.ViewModels;

namespace WinFinger.Views.Pages;

public partial class NotesPage : UserControl, IIslandPage
{
    private AppViewModel? _model;
    private Note? _current;
    private bool _loadingEditor;
    private readonly DispatcherTimer _saveDebounce;

    public NotesPage()
    {
        InitializeComponent();
        // mac: 300 ms autosave debounce
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            CommitEditor();
        };
    }

    public void Initialize(AppViewModel model)
    {
        _model = model;
        NoteList.ItemsSource = model.Notes.Notes;

        NewButton.Click += (_, _) => CreateNote();
        EmptyNewButton.Click += (_, _) => CreateNote();
        NoteList.SelectionChanged += (_, _) => LoadEditor(NoteList.SelectedItem as Note);
        PinButton.Click += (_, _) =>
        {
            if (_current is { } note)
            {
                model.Notes.TogglePin(note);
                RefreshPinButton();
            }
        };

        TitleBox.TextChanged += OnEditorChanged;
        BodyBox.TextChanged += OnEditorChanged;
        model.NewNoteRequested += CreateNote;
        model.Notes.Notes.CollectionChanged += OnNotesChanged;

        RefreshListEmptyHint();
        // mac onAppear: auto-select the first note
        if (NoteList.SelectedItem is null && model.Notes.Notes.FirstOrDefault() is { } first)
            NoteList.SelectedItem = first;
    }

    public void OnShown()
    {
        if (_model is null) return;
        if (NoteList.SelectedItem is null && _model.Notes.Notes.FirstOrDefault() is { } first)
            NoteList.SelectedItem = first;
    }

    private void OnNotesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshListEmptyHint();
        if (_model is null) return;
        // mac .onChange(of: store.notes): selected note vanished → first note
        if (_current is not null && !_model.Notes.Notes.Contains(_current))
            NoteList.SelectedItem = _model.Notes.Notes.FirstOrDefault();
    }

    private void RefreshListEmptyHint()
    {
        bool empty = _model?.Notes.Notes.Count == 0;
        ListEmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateNote()
    {
        if (_model is null) return;
        var note = _model.Notes.Create();
        NoteList.SelectedItem = note;
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void LoadEditor(Note? note)
    {
        // flush pending edits of the previous note before switching
        if (_saveDebounce.IsEnabled)
        {
            _saveDebounce.Stop();
            CommitEditor();
        }

        _current = note;
        _loadingEditor = true;
        try
        {
            if (note is null)
            {
                EditorPane.Visibility = Visibility.Collapsed;
                EmptyPane.Visibility = Visibility.Visible;
                return;
            }
            EditorPane.Visibility = Visibility.Visible;
            EmptyPane.Visibility = Visibility.Collapsed;
            TitleBox.Text = note.Title;
            BodyBox.Text = note.Body;
            TitlePlaceholder.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshPinButton();
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    private void RefreshPinButton()
    {
        bool pinned = _current?.IsPinned == true;
        PinGlyph.Text = pinned ? "\uE840" : "\uE718";
        PinGlyph.SetResourceReference(TextBlock.ForegroundProperty, pinned ? "Brush.Teal" : "Brush.TextSecondary");
        PinButton.ToolTip = pinned ? "取消置顶" : "置顶便签";
    }

    private void OnEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (ReferenceEquals(sender, TitleBox))
            TitlePlaceholder.Visibility = TitleBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_loadingEditor || _current is null) return;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void CommitEditor()
    {
        if (_model is null || _current is null) return;
        _model.Notes.Update(_current.Id, TitleBox.Text, BodyBox.Text);
    }

    // ── row context menu ──

    private void OnContextPin(object sender, RoutedEventArgs e)
    {
        if (_model is null || ((FrameworkElement)sender).DataContext is not Note note) return;
        _model.Notes.TogglePin(note);
        if (ReferenceEquals(note, _current)) RefreshPinButton();
    }

    private void OnContextDelete(object sender, RoutedEventArgs e)
    {
        if (_model is null || ((FrameworkElement)sender).DataContext is not Note note) return;
        bool wasCurrent = ReferenceEquals(note, _current);
        _model.Notes.Remove(note);
        if (wasCurrent)
            NoteList.SelectedItem = _model.Notes.Notes.FirstOrDefault();
    }
}
