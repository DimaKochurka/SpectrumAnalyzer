using System;
using Avalonia.Controls;
using ScottPlot;
using SpectrumAnalyzer.ViewModels;

namespace SpectrumAnalyzer.Views;

public partial class AnalysisView : UserControl
{
    private ScottPlot.Plottables.Signal? _signalPlot;

    // Reuse the same data buffer for plot updates to avoid allocating
    // a new array for every incoming FFT frame.
    private double[] _renderData;

    public AnalysisView()
    {
        InitializeComponent();

        _renderData = new double[2048];

        InitializePlot();

        DataContextChanged += (s, e) =>
        {
            if (DataContext is AnalysisViewModel vm)
            {
                vm.RequestPlotUpdate += UpdateGraph;
                Unloaded += (_, _) => vm.StopAnalysis();
            }
        };
    }

    private void InitializePlot()
    {
        SpectrumPlot.Plot.FigureBackground.Color = Color.FromHex("#1e1e1e");
        SpectrumPlot.Plot.DataBackground.Color = Color.FromHex("#2d2d2d");
        SpectrumPlot.Plot.Axes.Color(Color.FromHex("#d7d7d7"));
        SpectrumPlot.Plot.Grid.MajorLineColor = Color.FromHex("#404040");

        SpectrumPlot.Plot.Title("Spectrum Analysis");
        SpectrumPlot.Plot.YLabel("Amplitude (dB)");
        SpectrumPlot.Plot.XLabel("Frequency (Hz)");

        double period = 44100.0 / 4096.0;

        _signalPlot = SpectrumPlot.Plot.Add.Signal(_renderData, period);

        _signalPlot.Color = Color.FromHex("#00f2fe");
        _signalPlot.LineWidth = 1.5f;

        SpectrumPlot.Plot.Axes.SetLimits(0, 22050, -100, 10);

        SpectrumPlot.UserInputProcessor.Disable();

        SpectrumPlot.Refresh();
    }

    private void UpdateGraph(double[] fftDataDb)
    {
        if (_signalPlot == null || fftDataDb.Length == 0) return;

        int lengthToCopy = Math.Min(fftDataDb.Length, _renderData.Length);
        Array.Copy(fftDataDb, _renderData, lengthToCopy);

        SpectrumPlot.Refresh();
    }
}