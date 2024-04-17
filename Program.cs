using Newtonsoft.Json;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using Aspose.Email;
using Aspose.Email.Clients.Imap;
using Aspose.Email.Clients;

namespace TecieVRC
{
    internal class Program
    {
        private static int numThreads = 4;
        public static ApiClient client = new(); 
        static Configuration config = new()
        {
            Username = "TEC Con Staff",
            Password = Environment.GetEnvironmentVariable("TECMasterAuth"),
            UserAgent = "TecieBot/0.0.1 constaff"
        };

        static AuthenticationApi authApi = new(client, client, config);
        static GroupsApi groupsApi = new(client, client, config);

        static void Main(string[] args)
        {
            try
            {
                int i;
                Thread[]? servers = new Thread[numThreads];

                Console.WriteLine("\n*** Tecie VRChat ***\n");
                Console.WriteLine("Server started, waiting for client connect...\n");
                for (i = 0; i < numThreads; i++)
                {
                    servers[i] = new(ServerThread);
                    servers[i]?.Start();
                }

                ApiResponse<CurrentUser> currentUserResp = authApi.GetCurrentUserWithHttpInfo();
                Thread.Sleep(500);

                if (requiresEmail2FA(currentUserResp))
                {
                    using (var client = new ImapClient("imap.gmail.com", 993, "theenergeticconvention@gmail.com", Environment.GetEnvironmentVariable("SMTPPASS")))
                    {
                        client.SecurityOptions = SecurityOptions.SSLImplicit;
                        client.SelectFolder("Inbox");

                        i = 0;
                        foreach (ImapMessageInfo messageInfo in client.ListMessages().OrderByDescending<ImapMessageInfo, DateTime>((message) => { return message.Date; }))
                        {
                            var eml = client.FetchMessage(messageInfo.UniqueId);
                            authApi.Verify2FAEmailCode(new TwoFactorEmailCode(eml.Body.Substring(eml.Body.IndexOf("code: ") + 6, 6)));
                            i++;
                            if (i == 1) { break; }
                        }
                    }
                }
                else { authApi.Verify2FA(new TwoFactorAuthCode("123456")); }

                CurrentUser currentUser = authApi.GetCurrentUser();
                Console.WriteLine("Logged in as {0}", currentUser.DisplayName);

                Thread.Sleep(250);
                while (i > 0)
                {
                    for (int j = 0; j < numThreads; j++)
                    {
                        if (servers[j] != null)
                        {
                            if (servers[j]!.Join(50))
                            {
                                Console.WriteLine($"Server thread[{servers[j]!.ManagedThreadId}] finished.");
                                servers[j] = new Thread(ServerThread);
                                servers[j]?.Start();
                            }
                        }
                    }
                }
                Console.WriteLine("\nServer threads exhausted, exiting.");
            }
            catch (ApiException ex)
            {
                Console.WriteLine("Exception when calling API: {0}", ex.Message);
                Console.WriteLine("Status Code: {0}", ex.ErrorCode);
                Console.WriteLine(ex.ToString());
            }
        }

        static bool requiresEmail2FA(ApiResponse<CurrentUser> resp)
        {
            if (resp.RawContent.Contains("emailOtp")) { return true; }
            return false;
        }

        private static void ServerThread()
        {
            NamedPipeServerStream pipeServer =
                new NamedPipeServerStream("TecieVRCPipe", PipeDirection.InOut, numThreads);

            int threadId = Thread.CurrentThread.ManagedThreadId;

            // Wait for a client to connect
            pipeServer.WaitForConnection();

            Console.WriteLine($"Client connected on thread[{threadId}].");
            try
            {
                // Read the request from the client. Once the client has
                // written to the pipe its security token will be available.

                StreamString ss = new StreamString(pipeServer);
                string authkey = Environment.GetEnvironmentVariable("TECKEY") ?? "no key found";

                // Verify our identity to the connected client using a
                // string that the client anticipates.
                if (ss.ReadString() != authkey) { ss.WriteString("Unauthorized client!"); throw new Exception("Unauthorized client connection attemted!"); }
                ss.WriteString(authkey);
                string operation = ss.ReadString(); // E for event ping  A for announcement  U for update

                string post = "";
                ss.WriteString("READY");
                string message = ss.ReadString();

                switch (operation)
                {
                    case "A":
                        post = message;
                        groupsApi.CreateGroupAnnouncement("grp_68436afe-3d3a-4455-b88c-c8fa6924e4e0", new CreateGroupAnnouncementRequest("Announcement", post, null, true));
                        ss.WriteString("SUCCESS");
                        break;
                    case "E":
                        EventPingInfo eventinfo = JsonConvert.DeserializeObject<EventPingInfo>(message)!;
                        Console.WriteLine(JsonConvert.SerializeObject(eventinfo, Formatting.Indented));
                        post = $"{eventinfo.EventDescription}\n\n{(eventinfo.EventLink != null ? $"Join Here! {eventinfo.EventLink}\nSee the current event here: https://thenergeticon.com/Events/currentevent" : "See the current event here: https://thenergeticon.com/Events/currentevent")}";
                        groupsApi.CreateGroupAnnouncement("grp_68436afe-3d3a-4455-b88c-c8fa6924e4e0", new CreateGroupAnnouncementRequest($"{eventinfo.EventName} is starting!", post, null, true));
                        ss.WriteString("SUCCESS");
                        break;
                    case "U":
                        post = message;
                        groupsApi.CreateGroupAnnouncement("grp_68436afe-3d3a-4455-b88c-c8fa6924e4e0", new CreateGroupAnnouncementRequest("Update", post, null, true));
                        ss.WriteString("SUCCESS");
                        break;
                    default:
                        Console.WriteLine("Invalid operation");
                        ss.WriteString("FAILURE");
                        break;
                }
            }
            // Catch any exception thrown just in case sumn happens
            catch (Exception e)
            {
                Console.WriteLine($"ERROR: {e.Message}");
            }
            pipeServer.Close();
        }
    }

    class EventPingInfo(string name, string desc, string? link)
    {
        public string EventName = name;
        public string EventDescription = desc;
        public string? EventLink = link;
    }

    public class StreamString
    {
        private Stream ioStream;
        private UnicodeEncoding streamEncoding;

        public StreamString(Stream ioStream)
        {
            this.ioStream = ioStream;
            streamEncoding = new UnicodeEncoding();
        }

        public string ReadString()
        {
            int len = 0;

            len = ioStream.ReadByte() * 256;
            len += ioStream.ReadByte();
            byte[] inBuffer = new byte[len];
            ioStream.Read(inBuffer, 0, len);

            return streamEncoding.GetString(inBuffer);
        }

        public int WriteString(string outString)
        {
            byte[] outBuffer = streamEncoding.GetBytes(outString);
            int len = outBuffer.Length;
            if (len > UInt16.MaxValue)
            {
                len = (int)UInt16.MaxValue;
            }
            ioStream.WriteByte((byte)(len / 256));
            ioStream.WriteByte((byte)(len & 255));
            ioStream.Write(outBuffer, 0, len);
            ioStream.Flush();

            return outBuffer.Length + 2;
        }
    }
}
