namespace SqliteInspector.Sample;

public partial class MainPage : ContentPage
{
    private readonly NoteDatabase _db;

    public MainPage(NoteDatabase db)
    {
        InitializeComponent();
        _db = db;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadNotesAsync();
    }

    private async Task LoadNotesAsync()
    {
        var notes = await _db.GetAllAsync();
        NotesCollection.ItemsSource = notes;
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        var title = await DisplayPromptAsync("New Note", "Enter a title:");
        if (string.IsNullOrWhiteSpace(title))
            return;

        await _db.AddAsync(title);
        await LoadNotesAsync();
    }

    private async void OnDeleteSwipe(object? sender, EventArgs e)
    {
        if (sender is SwipeItem { BindingContext: Note note })
        {
            await _db.DeleteAsync(note.Id);
            await LoadNotesAsync();
        }
    }
}
