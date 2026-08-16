using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using RIPDF.Models;
using RIPDF.Services;

namespace RIPDF;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly PdfRasterizerService _rasterizerService = new();
    private readonly int[] _dpiPresets = { 72, 100, 150, 200, 300, 400, 600, 800, 1000, 1200 };
    private CancellationTokenSource? _cts;

    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private int _dpi = 300;
    private string _dpiInputText = "300";
    private int _jpegQuality = 85;
    private double _progressValue;
    private string _statusMessage = "Готов к конвертации.";
    private bool _isRasterizationEnabled = true;
    private bool _isCheckFillEnabled = false;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        DpiInputText = Dpi.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int[] DpiPresets => _dpiPresets;

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
        set
        {
            if (_dpi == value)
            {
                return;
            }

            _dpi = value;
            _dpiInputText = value.ToString();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Dpi)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDpiPresetIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DpiInputText)));
        }
    }

    public int SelectedDpiPresetIndex
    {
        get
        {
            var nearestIndex = 0;
            var minDifference = int.MaxValue;

            for (var index = 0; index < _dpiPresets.Length; index++)
            {
                var difference = Math.Abs(_dpiPresets[index] - _dpi);
                if (difference < minDifference)
                {
                    minDifference = difference;
                    nearestIndex = index;
                }
            }

            return nearestIndex;
        }
        set
        {
            if (value < 0 || value >= _dpiPresets.Length)
            {
                return;
            }

            Dpi = _dpiPresets[value];
        }
    }

    public string DpiInputText
    {
        get => _dpiInputText;
        set
        {
            if (_dpiInputText == value)
            {
                return;
            }

            _dpiInputText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DpiInputText)));

            if (int.TryParse(value, out var parsedValue) && parsedValue > 0)
            {
                Dpi = parsedValue;
            }
        }
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

    public bool IsRasterizationEnabled
    {
        get => _isRasterizationEnabled;
        set => SetProperty(ref _isRasterizationEnabled, value);
    }

    public bool IsCheckFillEnabled
    {
        get => _isCheckFillEnabled;
        set => SetProperty(ref _isCheckFillEnabled, value);
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

        if (!IsRasterizationEnabled && !IsCheckFillEnabled)
        {
            MessageBox.Show(this, "Выберите хотя бы одно действие: растрирование или проверку заливки.", "Валидация", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        ProgressValue = 0;
        StatusMessage = "Запуск...";

        try
        {
            if (IsCheckFillEnabled)
            {
                StatusMessage = "Проверка заливки PDF...";
                var statusProgress = new Progress<string>(message => StatusMessage = message);
                var progress = new Progress<double>(value => ProgressValue = Math.Min(50, value * 50));

                var fillResults = await _rasterizerService.CheckFillAsync(InputPath, Dpi, statusProgress, progress, _cts.Token);
                
                var resultMessage = "Результаты проверки заливки:\n\n";
                foreach (var page in fillResults.OrderBy(x => x.Key))
                {
                    resultMessage += $"Страница {page.Key}: {page.Value:F2}%\n";
                }

                MessageBox.Show(this, resultMessage, "Проверка заливки", MessageBoxButton.OK, MessageBoxImage.Information);

                if (!IsRasterizationEnabled)
                {
                    StatusMessage = "Готов к конвертации.";
                    return;
                }

                ProgressValue = 50;
            }

            if (IsRasterizationEnabled)
            {
                StatusMessage = "Запуск растрирования...";
                var options = new RasterizationOptions(InputPath, OutputPath, Dpi, JpegQuality);
                var statusProgress = new Progress<string>(message => StatusMessage = message);
                var progress = new Progress<double>(value => 
                {
                    var baseProgress = IsCheckFillEnabled ? 50 : 0;
                    var maxProgress = IsCheckFillEnabled ? 100 : 100;
                    ProgressValue = baseProgress + (value * (maxProgress - baseProgress));
                });

                await _rasterizerService.RasterizeAsync(options, statusProgress, progress, _cts.Token);
                StatusMessage = "Конвертация успешно завершена.";
                MessageBox.Show(this, "Готово. PDF с JPEG-страницами создан.", "RIPDF", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Операция отменена.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Ошибка операции.";
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
