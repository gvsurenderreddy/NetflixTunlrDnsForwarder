using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using ARSoft.Tools.Net.Dns;

namespace NetflixTunlrDnsForwarder {
    class Program {
        private static DnsClient tunlrDnsClient;
        static readonly DnsClient googleDnsClient = new DnsClient(new List<IPAddress> { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4") }, 3000);
        static void Main(String[] args) {
            const Int32 maximumRestarts = 10;
            for (var restartCount = 0; restartCount != maximumRestarts; ++restartCount)
                try {
                    using (var webClient = new WebClient { Proxy = null }) {
                        Console.Write("Retrieving Tunlr DNS server IPs...");
                        const String dnsServerLookupUrl = "http://tunlr.net/tunapi.php?action=getdns&version=1&format=json";
                        var html = webClient.DownloadString(dnsServerLookupUrl);
                        const String ipAddressRegexPattern = @"(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)";
                        var matches = Regex.Matches(html, ipAddressRegexPattern);
                        if (matches.Count != 2)
                            throw new Exception("Two IP addresses not found at " + dnsServerLookupUrl);
                        Console.WriteLine("done");
                        var tunlrDnsIps = new List<IPAddress> { IPAddress.Parse(matches[0].Value), IPAddress.Parse(matches[1].Value) };
                        Console.WriteLine("Tunlr DNS IPs found: {0}", String.Join(", ", tunlrDnsIps));
                        tunlrDnsClient = new DnsClient(tunlrDnsIps, 5000);
                    }
                    using (var dnsServer = new DnsServer(IPAddress.Any, 10, 10, ProcessQuery)) {
                        Console.Write("Starting DNS server...");
                        dnsServer.Start();
                        Console.WriteLine("done");
                        for (; ; )
                            Thread.Sleep(60000);
                    }
                }
                catch (Exception exception) {
                    Console.WriteLine(exception);
                    Console.Write("Attempting restart {0} of {1}", restartCount, maximumRestarts);
                }
        }
        static DnsMessageBase ProcessQuery(DnsMessageBase dnsMessage, IPAddress clientAddress, ProtocolType protocolType) {
            dnsMessage.IsQuery = false;
            DnsMessage query = dnsMessage as DnsMessage;
            if ((query != null) && (query.Questions.Count == 1)) {
                // Send query to upstream server
                DnsQuestion question = query.Questions[0];
                DnsClient dnsClient = question.Name.EndsWith("netflix.com") ? tunlrDnsClient : googleDnsClient;
                Console.WriteLine("{0:u} {1}\t -> {2}\t -> {3}", DateTime.Now, clientAddress, dnsClient == tunlrDnsClient ? "tunlr.net" : "Google DNS", question.Name);
                DnsMessage answer = dnsClient.Resolve(question.Name, question.RecordType, question.RecordClass);
                // If got an answer, copy it to the message sent to the client
                if (answer != null) {
                    foreach (DnsRecordBase record in (answer.AnswerRecords))
                        query.AnswerRecords.Add(record);
                    foreach (DnsRecordBase record in (answer.AdditionalRecords))
                        query.AnswerRecords.Add(record);
                    query.ReturnCode = ReturnCode.NoError;
                    return query;
                }
            }
            // Not a valid query or upstream server did not answer correctly
            dnsMessage.ReturnCode = ReturnCode.ServerFailure;
            return dnsMessage;
        }
    }
}
