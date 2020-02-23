using System;

namespace TouhouTrek.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            Lobby lobby = new Lobby();
            lobby.start();
            while (true)
            {
                string input = Console.ReadLine();
                switch (input)
                {
                    case "stop":
                        lobby.stop();
                        Console.WriteLine("按下任意键继续……");
                        Console.Read();
                        return;
                    default:
                        break;
                }
            }
        }
    }
}
