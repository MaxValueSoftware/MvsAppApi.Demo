using System;
using System.Linq;
using System.Security.Cryptography;
using MvsAppApi.Core;
using MvsAppApi.JsonAdapter;

namespace HelloWorld
{
    class Program
    {
        // NOTE: THIS IS ALL OUTDATED AND DOES NOT WORK (josh 2019/08/30)
        //       ITS PARTIALLY WORKING BUT HM3 RESTARTS IT SINCE NO SERVER PIPES (mike 2021/1/19)
        // todo: register/verify 'server' pipes too. these should listen for tracker requests and at least log them
        //       also, register callback for get_stats (or use a different method without callbacks)
        
        private const string DefaultApiVersion = "1.2";
        private const string AppId = "7FE40482-D120-B810-6886-57579DFCA02E";

        static void Main(string[] args)
        {
            var outboundRequestId = 0;
            var appName = "MVS API Demo - Sample App";

            IsHm3 = !args.Any(a => a.Equals("--tracker=pt4"));
            ApiVersion = DefaultApiVersion;
            var apiVersionArg = args.FirstOrDefault(a => a.StartsWith("--apiversion="));
            if (!string.IsNullOrEmpty(apiVersionArg))
                ApiVersion = apiVersionArg.Split('=')[1];


            var client = PipeStream.Create(IsHm3, ApiVersion, appName);

            // register the client

            var success = client.Register(++outboundRequestId, "1.0.0.0", out var responseStr, out var salt, out _, out _);
            Console.WriteLine("Response: " + responseStr);
            Console.WriteLine(success ? "Register: Success" : "Register: Fail");

            // verify the client

            if (success)
            {
                var hash = GetHash(salt);
                success = client.Verify(++outboundRequestId, hash, out responseStr, false, out _);
                Console.WriteLine("Response: " + responseStr);
                Console.WriteLine(success ? "Verify: Success" : "Verify: Fail");
            }

            // get list of stats 
            
            if (success)
            {
                // todo: allow choice of cash, tourney or both
                var tableType = "cash";
                success = client.GetStats(++outboundRequestId, tableType, true, out responseStr);
                Console.WriteLine(success ? "Verify: Success" : "Verify: Fail");
            }

            if (!success)
            {
                Console.WriteLine(responseStr);
                Console.WriteLine(@"Hmmmm? Something went wrong.");
            }
            else
                Console.WriteLine(@"Hello World!");

            Console.WriteLine("Press any key to stop...");
            Console.ReadKey();
        }

        private static string GetHash(string salt)
        {
            var hashAlgorithm = (HashAlgorithm)SHA512.Create();
            var hash = Hash.Calculate(hashAlgorithm, AppId, salt);
            return hash;
        }

        public static string ApiVersion { get; set; }

        public static bool IsHm3 { get; set; }
    }
}
