using System;
using NAudio.Wave;

namespace SpectrumAnalyzer.Services;

public class FileAudioPlayer : IDisposable
{
    private IWavePlayer _device;
    private AudioFileReader _reader;
    private readonly AudioService _analyzer;

    public event Action<double[]> OnDataAvailable;
    public event Action OnPlaybackStopped;

    public FileAudioPlayer(AudioService analyzer)
    {
        _analyzer = analyzer;
    }

    public void LoadAndPlay(string filePath)
    {
        try
        {
            Stop();

            if (!System.IO.File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Player] File not found: {filePath}");
                return;
            }

            _reader = new AudioFileReader(filePath);

            // Use the source file's actual sample rate for frequency calculations.
            _analyzer.SampleRate = _reader.WaveFormat.SampleRate;

            var aggregator = new SampleAggregator(_reader);
            aggregator.DataAvailable += Aggregator_DataAvailable;

            _device = new WaveOutEvent();
            _device.Init(aggregator);
            _device.PlaybackStopped += (s, e) => OnPlaybackStopped?.Invoke();

            _device.Play();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Player] Failed to play audio file: {ex.Message}");
        }
    }

    public void TogglePause()
    {
        if (_device == null)
        {
            return;
        }

        if (_device.PlaybackState == PlaybackState.Playing)
        {
            _device.Pause();
        }
        else if (_device.PlaybackState == PlaybackState.Paused)
        {
            _device.Play();
        }
    }

    public bool IsPlaying()
    {
        return _device?.PlaybackState == PlaybackState.Playing;
    }

    private void Aggregator_DataAvailable(object sender, double[] buffer)
    {
        OnDataAvailable?.Invoke(buffer);
    }

    public void Stop()
    {
        _device?.Stop();
        _device?.Dispose();
        _reader?.Dispose();

        _device = null;
        _reader = null;
    }

    public void Dispose()
    {
        Stop();
    }
}

public class SampleAggregator : ISampleProvider
{
    private readonly ISampleProvider _source;

    public event EventHandler<double[]> DataAvailable;

    public SampleAggregator(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);

        if (read > 0)
        {
            double[] processedData;

            // Create a mono analysis signal by averaging the left and right channels.
            // The original playback buffer is left unchanged.
            if (_source.WaveFormat.Channels == 2)
            {
                int monoCount = read / 2;
                processedData = new double[monoCount];

                for (int i = 0; i < monoCount; i++)
                {
                    float left = buffer[offset + i * 2];
                    float right = buffer[offset + i * 2 + 1];

                    processedData[i] = (left + right) / 2.0;
                }
            }
            else
            {
                processedData = new double[read];

                for (int i = 0; i < read; i++)
                {
                    processedData[i] = buffer[offset + i];
                }
            }

            DataAvailable?.Invoke(this, processedData);
        }

        return read;
    }
}