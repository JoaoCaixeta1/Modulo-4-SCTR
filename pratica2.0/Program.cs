using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

/// Implementação do Módulo 1b: simulador (mockup) de hardware de medição.
/// Este programa simula múltiplos IEDs (Intelligent Electronic Devices) gerando dados de telemetria de uma subestação de energia e os envia via UDP broadcast para que o Módulo 4 possa processá-los.

public class Modulo1b_Simulador
{
    /// Constantes de Configuração da Simulação. A porta UDP para a qual os pacotes de dados serão enviados.
    private const int PORTA_ENVIO = 54321;

    /// O intervalo em milissegundos entre o envio de cada pacote. Simula a taxa de amostragem contínua de um dispositivo real.
    private const int INTERVALO_MS = 100;

    /// Ponto de entrada principal do simulador.
    public static void Main(string[] args)
    {
        Console.WriteLine("Iniciando Módulo 1b (Simulador de MÚLTIPLOS IEDs)...");

        // UdpClient é o responsável pela comunicação de rede via UDP.
        using (var udpClient = new UdpClient())
        {
            // Habilita o modo broadcast, permitindo que o pacote seja enviado para todos os dispositivos na rede local, em vez de um destino específico.
            udpClient.EnableBroadcast = true;
            
            // Define o endereço de destino como o endereço de broadcast (255.255.255.255) na porta especificada.
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, PORTA_ENVIO);
            Console.WriteLine($"Enviando pacotes para o endereço {broadcastEndpoint} a cada {INTERVALO_MS}ms.");
            
            long numeroPacote = 0;
            var random = new Random();

            // Loop infinito que mantém o simulador rodando e enviando dados.
            while (true)
            {
                // Simulação de Múltiplos Dispositivos, onde a cada iteração, escolhe um ID aleatório entre 10, 11 e 12. Isso simula um ambiente com 3 IEDs diferentes
                int dispositivoIdAleatorio = 10 + random.Next(3);

                // Simula corrente (A) com valores que variam entre 150A e 300A.
                double correnteBase = 150.0;
                double correnteVariacao = random.NextDouble() * 150;
                double correnteFinal = correnteBase + correnteVariacao;

                // Simula tensão (V) com pequenas flutuações em torno de 220V.
                double tensaoBase = 220.0;
                double tensaoVariacao = (random.NextDouble() - 0.5) * 10; // Flutuação de +/- 5V
                double tensaoFinal = tensaoBase + tensaoVariacao;

                // Simula frequência (Hz) com pequenas flutuações em torno de 60Hz.
                double freqBase = 60.0;
                double freqVariacao = (random.NextDouble() - 0.5) * 0.5; // Flutuação de +/- 0.25Hz
                double freqFinal = freqBase + freqVariacao;

                // Simula temperatura de um equipamento, variando entre 85°C e 95°C.
                double tempBase = 85.0;
                double tempVariacao = random.NextDouble() * 10;
                double tempFinal = tempBase + tempVariacao;

                // Montagem do Pacote de Dados (JSON). Cria um objeto anônimo que representa a estrutura do nosso pacote de dados.
                // Os nomes das propriedades (IedId, Ia, etc.) devem corresponder exatamente ao que o Módulo 4 espera ler.
                var pacoteDados = new
                {
                    IedId = dispositivoIdAleatorio,
                    numPacote = numeroPacote++,
                    Ia = correnteFinal,
                    Ib = correnteFinal - 5, 
                    Ic = correnteFinal + 5,
                    Va = tensaoFinal,
                    Vb = tensaoFinal - 2,
                    Vc = tensaoFinal + 2,
                    Freq = freqFinal,
                    Temp = tempFinal
                };

                // Serializa o objeto C# para uma string no formato JSON.
                string jsonString = JsonSerializer.Serialize(pacoteDados);

                // Converte a string JSON para um array de bytes para envio pela rede.
                byte[] data = Encoding.UTF8.GetBytes(jsonString);

                try
                {
                    // Envia os dados pela rede.
                    udpClient.Send(data, data.Length, broadcastEndpoint);
                    
                    // Imprime um log no console para feedback visual do que está sendo enviado, agora com Tensão e Frequência.
                    Console.WriteLine($"Pacote {numeroPacote-1} enviado pelo IED {dispositivoIdAleatorio}. Ia = {correnteFinal:F2}, Va = {tensaoFinal:F2}, Freq = {freqFinal:F2}, Temp = {tempFinal:F2}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Erro ao enviar pacote: {e.Message}");
                }
                
                // Pausa a execução pela quantidade de tempo definida, controlando a "cadência" do envio.
                Thread.Sleep(INTERVALO_MS);
            }
        }
    }
}