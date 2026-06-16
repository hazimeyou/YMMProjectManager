using System;
using System.ComponentModel;
using System.Windows;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

public sealed class TimeLineSeek : ITimelineToolViewModel, INotifyPropertyChanged
{
    public TimeLineSeek() 
    { 
        MessageBox.Show("TimeLineSeek initialized.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Timeline Timeline { get; set; } = null!;

    public void SetTimelineToolInfo(TimelineToolInfo info)
    {
        Timeline = info.Timeline;
    }

    public void SeekToFrame(int targetFrame)
    {
        targetFrame = Math.Max(0, targetFrame);

        void DoSeek()
        {
            if (Timeline is null)
                return;

            Timeline.CurrentFrame = targetFrame;
        }

        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Invoke(DoSeek);
        else
            DoSeek();
    }
    public void SeekByFrames(int frameDelta)
    {
        Timeline.CurrentFrame += frameDelta;
    }

}