using System.IO;
using iTunesPod.Core;
using NAudio.Wave;

namespace iTunesPod.App;

public sealed class PlaybackController : IDisposable
{
    private WaveOutEvent? _output;
    private MediaFoundationReader? _reader;
    public Track? Current{get;private set;}
    public event Action? Changed;
    public event Action<Track>? Ended;
    bool _disposingAudio;
    public bool IsPlaying=>_output?.PlaybackState==PlaybackState.Playing;
    public double PositionSeconds=>_reader?.CurrentTime.TotalSeconds??0;
    public double TotalSeconds=>_reader?.TotalTime.TotalSeconds??0;
    private float _volume=.5f;
    public float Volume{get=>_volume;set{_volume=Math.Clamp(value,0,1);if(_output!=null)_output.Volume=_volume;}}
    public async Task PlayAsync(Track track)
    {
        if(!OperatingSystem.IsWindows())throw new NotSupportedException("当前试听引擎适用于 Windows。");
        if(!File.Exists(track.Path))throw new IOException("音乐文件已失效，设备可能已断开。");
        var reader=await Task.Run(()=>new MediaFoundationReader(track.Path));
        DisposeAudio();
        try
        {
            _reader=reader;_output=new WaveOutEvent{DesiredLatency=200,Volume=_volume};_output.Init(reader);
            _output.PlaybackStopped+=(sender,args)=>{Changed?.Invoke();if(!_disposingAudio&&ReferenceEquals(sender,_output)&&args.Exception==null&&_reader!=null&&_reader.Position>=_reader.Length)Ended?.Invoke(track);};Current=track;_output.Play();Changed?.Invoke();
        }
        catch{DisposeAudio();throw;}
    }
    public void Toggle(){if(_output==null)return;if(IsPlaying)_output.Pause();else{if(_reader!=null&&_reader.CurrentTime>=_reader.TotalTime)_reader.Position=0;_output.Play();}Changed?.Invoke();}
    public void Seek(double seconds){if(_reader!=null)_reader.CurrentTime=TimeSpan.FromSeconds(Math.Clamp(seconds,0,TotalSeconds));}
    public void Stop(){DisposeAudio();Current=null;Changed?.Invoke();}
    private void DisposeAudio(){_disposingAudio=true;try{_output?.Stop();_output?.Dispose();_output=null;_reader?.Dispose();_reader=null;}finally{_disposingAudio=false;}}
    public void Dispose()=>DisposeAudio();
    public static int DecodeProbe(string path)
    {using var reader=new MediaFoundationReader(path);return reader.Read(new byte[8192],0,8192);}
}

