using System;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScottPlot;
using SpectrumAnalyzer.Services;

namespace SpectrumAnalyzer.ViewModels;

public partial class AnalysisViewModel : ViewModelBase
{
    private readonly AudioService _audioService;
    private readonly FileAudioPlayer _player;
    private readonly DispatcherTimer _liveTimeoutTimer;

    private readonly UdpAudioReceiver _udpReceiver;
    private bool _isLiveMode = false;

    private double[] _liveAudioBuffer = new double[4096];
    private int _liveBufferIndex = 0;

    [ObservableProperty] private string _playPauseButtonText = "Pause";

    public event System.Action RequestNavigateToHome;
    public event System.Action<double[]> RequestPlotUpdate;

    private System.Collections.Generic.Dictionary<string, int> _predictionsCount;
    private int _validFramesCount;

    private double _maxPeakAmplitude;
    private double _maxDominantFrequency;
    private System.Collections.Generic.Dictionary<string, int> _structureVotes;
    private System.Collections.Generic.Dictionary<string, int> _timbreVotes;

    [ObservableProperty] private string _dominantFrequency = "---";
    [ObservableProperty] private string _peakAmplitude = "---";
    [ObservableProperty] private string _harmonicStructure = "---";
    [ObservableProperty] private string _timbreCheck = "---";
    [ObservableProperty] private string _predictedSource = "---";
    [ObservableProperty] private string _secondaryPredictedSource = "";

    [ObservableProperty] private string _sourceDirection = "---";

    // Direction stabilization settings.
    private const int DirectionUpdateIntervalMs = 500;
    private const double DirectionMajorityThreshold = 0.60;

    // Accumulated direction votes within the current time window.
    private int _leftDirectionVotes = 0;
    private int _centerDirectionVotes = 0;
    private int _rightDirectionVotes = 0;

    private DateTime _lastDirectionUpdate = DateTime.MinValue;

    public AnalysisViewModel()
    {
        _audioService = new AudioService();
        _player = new FileAudioPlayer(_audioService);

        _player.OnDataAvailable += HandleAudioData;
        _player.OnPlaybackStopped += FinalizeAnalysis;

        _udpReceiver = new UdpAudioReceiver();
        _udpReceiver.OnDataReceived += HandleIncomingUdpData;

        _liveTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _liveTimeoutTimer.Tick += CheckForLiveTimeout;
    }

    private void ResetStatistics()
    {
        _predictionsCount = new System.Collections.Generic.Dictionary<string, int>();
        _structureVotes = new System.Collections.Generic.Dictionary<string, int>();
        _timbreVotes = new System.Collections.Generic.Dictionary<string, int>();

        _maxPeakAmplitude = -1000;
        _maxDominantFrequency = 0;
        _validFramesCount = 0;
        ResetDirectionStabilization();

        DominantFrequency = "---";
        PeakAmplitude = "---";
        HarmonicStructure = "---";
        TimbreCheck = "---";
        PredictedSource = "---";
        SecondaryPredictedSource = "";
    }

    public void StartAnalysis(string filePath)
    {
        _isLiveMode = false;
        ResetStatistics();

        _liveBufferIndex = 0;
        Array.Clear(_liveAudioBuffer, 0, _liveAudioBuffer.Length);

        PlayPauseButtonText = "Pause";
        PredictedSource = "Analysis in progress...";
        SecondaryPredictedSource = "";


        _player.LoadAndPlay(filePath);
    }

    public void StartLiveAnalysis()
    {
        _isLiveMode = true;

        // The ESP32 live stream is sampled at 44.1 kHz.
        _audioService.SampleRate = 44100;
        ResetStatistics();

        PlayPauseButtonText = "Stop Live";
        PredictedSource = "Waiting for ESP32 audio stream...";

        _udpReceiver.StartListening();
        _liveTimeoutTimer.Start();
    }

    public void StopAnalysis()
    {
        if (_isLiveMode)
        {
            _udpReceiver.StopListening();
            _liveTimeoutTimer.Stop();
            ResetDirectionStabilization();
            FinalizeAnalysis();
        }
        else
        {
            _player.Stop();
        }
    }

    private void CheckForLiveTimeout(object? sender, EventArgs e)
    {
        if (_isLiveMode && _validFramesCount > 0)
        {
            if ((DateTime.Now - _lastPacketTime).TotalMilliseconds > 1200)
            {
                ResetDirectionStabilization();
                FinalizeAnalysis();
                _validFramesCount = 0;
            }
        }
    }

    private DateTime _lastPacketTime = DateTime.MinValue;

    private void ResetDirectionStabilization()
    {
        _leftDirectionVotes = 0;
        _centerDirectionVotes = 0;
        _rightDirectionVotes = 0;

        _lastDirectionUpdate = DateTime.UtcNow;
        SourceDirection = "---";
    }

    private void UpdateStableDirection(short sampleLag)
    {
        // Count each incoming direction estimate.
        if (sampleLag < -2)
            _leftDirectionVotes++;
        else if (sampleLag > 2)
            _rightDirectionVotes++;
        else
            _centerDirectionVotes++;

        DateTime now = DateTime.UtcNow;

        // Wait until the current voting window is complete.
        if ((now - _lastDirectionUpdate).TotalMilliseconds < DirectionUpdateIntervalMs)
            return;

        int totalVotes = _leftDirectionVotes
                       + _centerDirectionVotes
                       + _rightDirectionVotes;

        if (totalVotes > 0)
        {
            int maxVotes = Math.Max(
                _leftDirectionVotes,
                Math.Max(_centerDirectionVotes, _rightDirectionVotes));

            double majority = (double)maxVotes / totalVotes;

            if (majority >= DirectionMajorityThreshold)
            {
                if (maxVotes == _leftDirectionVotes)
                    SourceDirection = "Left";
                else if (maxVotes == _rightDirectionVotes)
                    SourceDirection = "Right";
                else
                    SourceDirection = "Center";
            }
        }

        // Start a new voting window.
        _leftDirectionVotes = 0;
        _centerDirectionVotes = 0;
        _rightDirectionVotes = 0;

        _lastDirectionUpdate = now;
    }

    private void HandleIncomingUdpData(short sampleLag, short[] left, short[] right)
    {
        // Discard a partially accumulated FFT frame after a gap in the UDP stream.
        if ((DateTime.Now - _lastPacketTime).TotalMilliseconds > 200)
        {
            _liveBufferIndex = 0;
        }
        _lastPacketTime = DateTime.Now;

        // The ESP32 estimates the inter-channel sample lag using cross-correlation.
        // Its sign indicates whether the source is closer to the left or right microphone.
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isLiveMode)
                UpdateStableDirection(sampleLag);
        });

        // Accumulate the left microphone channel into a full 4096-sample FFT frame.
        // Stereo information is used separately for direction estimation.
        for (int i = 0; i < left.Length; i++)
        {
            // Normalize 16-bit PCM samples to approximately -1.0 ... +1.0.
            _liveAudioBuffer[_liveBufferIndex] = left[i] / 32768.0;
            _liveBufferIndex++;

            if (_liveBufferIndex >= 4096)
            {
                double[] frameToProcess = new double[4096];
                Array.Copy(_liveAudioBuffer, frameToProcess, 4096);

                _liveBufferIndex = 0;

                HandleAudioData(frameToProcess);
            }
        }
    }

    private void HandleAudioData(double[] rawData)
    {
        // Extract spectral features from the current audio frame.
        double[] fftData = _audioService.ProcessFft(rawData, out double centroid);
        double freq = _audioService.GetDominantFrequency(fftData);
        double maxDb = fftData.Max();
        string structure = _audioService.GetAudioStructure(fftData);

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (maxDb < -60)
            {
                DominantFrequency = "...";
                PeakAmplitude = "---";
                TimbreCheck = "Silence";
                HarmonicStructure = "---";
            }
            else
            {
                DominantFrequency = $"{freq:F0} Hz";
                PeakAmplitude = $"{maxDb:F1} dB";
                HarmonicStructure = structure;

                string currentTimbre;
                if (centroid < 800) currentTimbre = "Deep / Warm";
                else if (centroid < 2500) currentTimbre = "Balanced";
                else currentTimbre = "Bright / Sharp";

                TimbreCheck = currentTimbre;

                if (maxDb > _maxPeakAmplitude) _maxPeakAmplitude = maxDb;
                if (freq > _maxDominantFrequency) _maxDominantFrequency = freq;

                // Accumulate frame-level results so the final classification is based
                // on the dominant result across the complete analyzed signal.
                if (!_structureVotes.ContainsKey(structure)) _structureVotes[structure] = 0;
                _structureVotes[structure]++;

                if (!_timbreVotes.ContainsKey(currentTimbre)) _timbreVotes[currentTimbre] = 0;
                _timbreVotes[currentTimbre]++;

                string currentPrediction = _audioService.PredictSource(freq, centroid, structure, maxDb);
                
                if (!_predictionsCount.ContainsKey(currentPrediction)) _predictionsCount[currentPrediction] = 0;
                _predictionsCount[currentPrediction]++;

                _validFramesCount++;
            }

            RequestPlotUpdate?.Invoke(fftData);
        });
    }

    private void FinalizeAnalysis()
    {
        if (_validFramesCount == 0 || _predictionsCount == null || _predictionsCount.Count == 0)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                PredictedSource = "Unable to determine (silence)";
                SecondaryPredictedSource = "";
            });
            return;
        }

        // Select the most frequently detected source across all valid frames.
        // The displayed confidence is the share of frames assigned to that source.
        var orderedPredictions = _predictionsCount.OrderByDescending(x => x.Value).ToList();

        var winnerSource = orderedPredictions.First();
        var winnerStructure = _structureVotes.OrderByDescending(x => x.Value).First().Key;
        var winnerTimbre = _timbreVotes.OrderByDescending(x => x.Value).First().Key;

        int currentFrames = _validFramesCount;

        double confidencePercent = ((double)winnerSource.Value / currentFrames) * 100;

        string runnerUpText = "";
        if (orderedPredictions.Count > 1)
        {
            var runnerUp = orderedPredictions[1];
            double runnerUpConfidence = ((double)runnerUp.Value / currentFrames) * 100;

            if (runnerUpConfidence >= 5)
            {
                runnerUpText = $"Alternative: {runnerUp.Key} ({runnerUpConfidence:F0}%)";
            }
        }

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            PredictedSource = $"{winnerSource.Key} (Confidence: {confidencePercent:F0}%)";
            SecondaryPredictedSource = runnerUpText;

            DominantFrequency = $"Max: {_maxDominantFrequency:F0} Hz";
            PeakAmplitude = $"Peak: {_maxPeakAmplitude:F1} dB";
            HarmonicStructure = $"Primary: {winnerStructure}";
            TimbreCheck = $"Predominant: {winnerTimbre}";
        });
    }

    [RelayCommand]
    public void TogglePlayPause()
    {
        if (_isLiveMode)
        {
            StopAnalysis();
        }
        else
        {
            _player.TogglePause();
            PlayPauseButtonText = _player.IsPlaying() ? "Pause" : "Resume";
        }
    }

    [RelayCommand]
    public void ReturnToHome()
    {
        StopAnalysis();
        ResetStatistics();
        RequestNavigateToHome?.Invoke();
    }
}