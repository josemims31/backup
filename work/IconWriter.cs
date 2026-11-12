using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class BackupIconWriter
{
    public static void Main(string[] args){File.WriteAllBytes(args[1],Convert(args[0]));}
    public static byte[] Convert(string path)
    {
        int[] sizes={16,20,24,32,40,48,64,96,128,256};var frames=new byte[sizes.Length][];
        using(var source=new Bitmap(path))for(int index=0;index<sizes.Length;index++){
            int size=sizes[index];using(var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb)){
                using(var graphics=Graphics.FromImage(bitmap)){graphics.Clear(Color.Transparent);graphics.CompositingMode=CompositingMode.SourceCopy;graphics.CompositingQuality=CompositingQuality.HighQuality;graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.PixelOffsetMode=PixelOffsetMode.HighQuality;using(var attrs=new ImageAttributes()){attrs.SetWrapMode(WrapMode.TileFlipXY);graphics.DrawImage(source,new Rectangle(0,0,size,size),0,0,source.Width,source.Height,GraphicsUnit.Pixel,attrs);}}
                using(var bytes=new MemoryStream()){
                    if(size==256)bitmap.Save(bytes,ImageFormat.Png);
                    else using(var writer=new BinaryWriter(bytes)){
                        int maskStride=((size+31)/32)*4;writer.Write(40);writer.Write(size);writer.Write(size*2);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(0);writer.Write(size*size*4);writer.Write(0);writer.Write(0);writer.Write(0);writer.Write(0);
                        for(int y=size-1;y>=0;y--)for(int x=0;x<size;x++){var c=bitmap.GetPixel(x,y);writer.Write(c.A==0?(byte)0:c.B);writer.Write(c.A==0?(byte)0:c.G);writer.Write(c.A==0?(byte)0:c.R);writer.Write(c.A);}
                        for(int y=size-1;y>=0;y--){var mask=new byte[maskStride];for(int x=0;x<size;x++)if(bitmap.GetPixel(x,y).A==0)mask[x/8]|=(byte)(128>>(x%8));writer.Write(mask);}
                    }
                    frames[index]=bytes.ToArray();
                }
            }
        }
        using(var output=new MemoryStream())using(var writer=new BinaryWriter(output)){
            writer.Write((ushort)0);writer.Write((ushort)1);writer.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++){writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(frames[i].Length);writer.Write(offset);offset+=frames[i].Length;}
            foreach(var frame in frames)writer.Write(frame);return output.ToArray();
        }
    }
}
