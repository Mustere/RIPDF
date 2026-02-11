using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using RIPDF.Models;
using RIPDF.Services;

namespace RIPDF;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly PdfRasterizerService _rasterizerService = new();
    private CancellationTokenSource? _cts;

    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private int _dpi = 300;
    private int _jpegQuality = 85;
    private double _progressValue;
    private string _statusMessage = "Готов к конвертации.";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string InputPath
    {
        get => _inputPath;
        set => SetProperty(ref _inputPath, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public int Dpi
    {
        get => _dpi;
        set => SetProperty(ref _dpi, value);
    }

    public int JpegQuality
    {
        get => _jpegQuality;
        set => SetProperty(ref _jpegQuality, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private void SelectInputFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PDF файлы (*.pdf)|*.pdf",
            Title = "Выберите многостраничный PDF"
        };

        if (dialog.ShowDialog() is true)
        {
            InputPath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                var directory = Path.GetDirectoryName(dialog.FileName) ?? Environment.CurrentDirectory;
                var fileName = Path.GetFileNameWithoutExtension(dialog.FileName);
                OutputPath = Path.Combine(directory, $"{fileName}_jpeg.pdf");
            }
        }
    }

    private void SelectOutputFile_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PDF файлы (*.pdf)|*.pdf",
            Title = "Куда сохранить PDF",
            FileName = string.IsNullOrWhiteSpace(OutputPath) ? "output_jpeg.pdf" : Path.GetFileName(OutputPath)
        };

        if (dialog.ShowDialog() is true)
        {
            OutputPath = dialog.FileName;
        }
    }

    private async void RunConversion_OnClick(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs())
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        ProgressValue = 0;
        StatusMessage = "Запуск...";

        var options = new RasterizationOptions(InputPath, OutputPath, Dpi, JpegQuality);
        var statusProgress = new Progress<string>(message => StatusMessage = message);
        var progress = new Progress<double>(value => ProgressValue = Math.Min(100, Math.Max(0, value * 100)));

        try
        {
            await _rasterizerService.RasterizeAsync(options, statusProgress, progress, _cts.Token);
            StatusMessage = "Конвертация успешно завершена.";
            MessageBox.Show(this, "Готово. PDF с JPEG-страницами создан.", "RIPDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Операция отменена.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Ошибка конвертации.";
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelConversion_OnClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private bool ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(InputPath) || !File.Exists(InputPath))
        {
            MessageBox.Show(this, "Укажите существующий входной PDF.", "Валидация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            MessageBox.Show(this, "Укажите путь для выходного файла.", "Валидация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
