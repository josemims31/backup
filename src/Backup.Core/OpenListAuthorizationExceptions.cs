using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
namespace Backup.Core
{
    public class OpenListAuthorizationException:IOException
    {
        public readonly bool RequiresTwoFactor;
        internal readonly bool TokenRejected;
        public OpenListAuthorizationException(bool otp):this(otp,otp?"OpenList 需要二步验证码；未启用2FA的账号无需验证码。":"OpenList 尚未授权或授权已失效，请编辑仓库并保存以完成授权。"){}
        protected internal OpenListAuthorizationException(bool otp,string message,bool tokenRejected=false):base(message){RequiresTwoFactor=otp;TokenRejected=tokenRejected;}
    }
    public sealed class OpenListLoginThrottledException:OpenListAuthorizationException
    {public OpenListLoginThrottledException():base(false,"OpenList 已暂时限制登录。请从最后一次429提示起至少等待5分钟后再授权；期间请勿反复登录，以免延长限制。"){} }
}
