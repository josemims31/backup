using System;
using System.Collections.Generic;
using System.Linq;
namespace Backup.Core
{
    public sealed class BandwidthPeriod
    {
        public int Days=127,StartMinute,EndMinute=1440;
        public decimal? UploadMbps,DownloadMbps;
    }
    public sealed class BandwidthSchedule
    {
        public List<BandwidthPeriod> Periods=new List<BandwidthPeriod>();
        public BandwidthSchedule ValidatedCopy()
        {
            var result=new BandwidthSchedule();
            foreach(var value in Periods??new List<BandwidthPeriod>())
            {
                if(value==null||value.Days<=0||(value.Days&~127)!=0)throw new ArgumentException("每个限速时段请至少选择一天。");
                if(value.StartMinute<0||value.StartMinute>=1440||value.EndMinute<=value.StartMinute||value.EndMinute>1440)throw new ArgumentException("限速时段的结束时间应晚于开始时间；跨天请分成两个时段。");
                if(value.UploadMbps<0||value.DownloadMbps<0)throw new ArgumentException("限速值不能小于 0；留空表示不限速。");
                result.Periods.Add(new BandwidthPeriod{Days=value.Days,StartMinute=value.StartMinute,EndMinute=value.EndMinute,UploadMbps=value.UploadMbps,DownloadMbps=value.DownloadMbps});
            }
            for(int i=0;i<result.Periods.Count;i++)for(int j=i+1;j<result.Periods.Count;j++)
            {
                var a=result.Periods[i];var b=result.Periods[j];
                if((a.Days&b.Days)!=0&&a.StartMinute<b.EndMinute&&b.StartMinute<a.EndMinute)throw new ArgumentException("第 "+(i+1)+"、"+(j+1)+" 个限速时段在同一天重叠，请调整时间。");
            }
            return result;
        }
        public BandwidthLimit At(DateTime localTime)
        {
            int minute=localTime.Hour*60+localTime.Minute,day=1<<(int)localTime.DayOfWeek;
            var period=Periods.FirstOrDefault(value=>(value.Days&day)!=0&&value.StartMinute<=minute&&minute<value.EndMinute);
            return period==null?new BandwidthLimit():new BandwidthLimit{UploadMbps=period.UploadMbps,DownloadMbps=period.DownloadMbps};
        }
        public DateTime NextBoundary(DateTime localTime)
        {
            DateTime next=localTime.Date.AddDays(1);
            foreach(var value in Periods)if((value.Days&(1<<(int)localTime.DayOfWeek))!=0)
            {
                foreach(int minute in new[]{value.StartMinute,value.EndMinute}){DateTime time=localTime.Date.AddMinutes(minute);if(time>localTime&&time<next)next=time;}
            }
            return next;
        }
    }
    public sealed class BandwidthLimit
    {
        public decimal? UploadMbps,DownloadMbps;
        public bool Paused{get{return UploadMbps==0||DownloadMbps==0;}}
        internal bool Same(BandwidthLimit other){return UploadMbps==other.UploadMbps&&DownloadMbps==other.DownloadMbps;}
    }
}
