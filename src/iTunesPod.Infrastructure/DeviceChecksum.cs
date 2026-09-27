using System.Buffers.Binary;
using System.Security.Cryptography;
namespace iTunesPod.Infrastructure;

/// <summary>HASH58 protocol implementation. AES substitutions are generated mathematically, not copied tables.</summary>
public static class DeviceChecksum
{
    static readonly byte[] Forward=BuildSbox();
    static readonly byte[] Reverse=Enumerable.Range(0,256).Select(i=>(byte)Array.IndexOf(Forward,(byte)i)).ToArray();
    static byte Multiply(byte a,byte b){byte sum=0;while(b>0){if((b&1)!=0)sum^=a;var high=(a&128)!=0;a<<=1;if(high)a^=0x1b;b>>=1;}return sum;}
    static byte[] BuildSbox(){var values=new byte[256];for(var i=0;i<256;i++){byte inv=0;if(i!=0){inv=1;for(var power=0;power<254;power++)inv=Multiply(inv,(byte)i);}byte Rotate(byte value,int n)=>(byte)((value<<n)|(value>>(8-n)));values[i]=(byte)(inv^Rotate(inv,1)^Rotate(inv,2)^Rotate(inv,3)^Rotate(inv,4)^0x63);}return values;}
    static int Gcd(int a,int b){while(b!=0){(a,b)=(b,a%b);}return a;}
    public static byte[] Hash58(byte[] source,ReadOnlySpan<byte> firewireId){
        if(source.Length<108||!source.AsSpan(0,4).SequenceEqual("mhbd"u8)||firewireId.Length!=8)throw new InvalidDataException("HASH58 要求有效数据库与 8 字节设备标识。");
        var data=(byte[])source.Clone();data.AsSpan(24,8).Clear();data.AsSpan(50,20).Clear();data.AsSpan(88,20).Clear();BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48,2),1);
        var keyInput=new byte[34];Convert.FromHexString("6723FE304533F890992107C1D012B2A10781").CopyTo(keyInput,0);
        for(var i=0;i<4;i++){var a=firewireId[i*2];var b=firewireId[i*2+1];var lcm=a==0||b==0?1:a*b/Gcd(a,b);var high=lcm>>8;var low=lcm&255;var at=18+i*4;keyInput[at]=Forward[high];keyInput[at+1]=Reverse[high];keyInput[at+2]=Forward[low];keyInput[at+3]=Reverse[low];}
        return HMACSHA1.HashData(SHA1.HashData(keyInput),data);
    }
    public static byte[] SignHash58(byte[] source,ReadOnlySpan<byte> firewireId){var signed=(byte[])source.Clone();BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(48,2),1);Hash58(signed,firewireId).CopyTo(signed,88);return signed;}
    public static bool VerifyHash58(byte[] source,ReadOnlySpan<byte> firewireId)=>source.Length>=108&&CryptographicOperations.FixedTimeEquals(Hash58(source,firewireId),source.AsSpan(88,20));
}
