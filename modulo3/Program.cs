// --- Bibliotecas Utilizadas ---
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// Implementação de um "mini Módulo 3": um ouvinte de relatórios para testes.
/// O único propósito deste programa é atuar como um receptor para os relatórios
/// periódicos enviados pelo Módulo 4, validando que a comunicação está funcionando
/// e que o formato dos dados está correto. Ele simula o papel do Módulo 3 real.
/// </summary>
public class Modulo3
{
    /// <summary>
    /// A porta UDP na qual este programa irá escutar por relatórios.
    /// DEVE ser a mesma porta para a qual o Módulo 4 está enviando (12000).
    /// </summary>
    private const int PORTA_ESCUTA = 12000;

    /// <summary>
    /// Ponto de entrada principal do ouvinte de relatórios.
    /// </summary>
    public static void Main(string[] args)
    {
        Console.WriteLine("--- Módulo 3 (Ouvinte de Relatórios) ---");
        Console.WriteLine($"Aguardando relatórios na porta UDP {PORTA_ESCUTA}...");

        // UdpClient é o responsável pela comunicação de rede via UDP.
        // Ao criá-lo passando a porta, ele automaticamente se "amarra" (bind) a ela,
        // pronto para receber dados.
        using (var udpClient = new UdpClient(PORTA_ESCUTA))
        {
            // IPEndPoint é usado para guardar informações de endereço e porta.
            // IPAddress.Any significa que aceitaremos pacotes de qualquer endereço de rede.
            var remoteEP = new IPEndPoint(IPAddress.Any, PORTA_ESCUTA);

            // Loop infinito que mantém o ouvinte ativo.
            while (true)
            {
                try
                {
                    // A chamada .Receive() é bloqueante: ela pausa a execução do programa
                    // aqui e fica esperando até que um pacote de dados chegue na porta.
                    byte[] receivedBytes = udpClient.Receive(ref remoteEP);
                    
                    // Converte o array de bytes recebido de volta para uma string JSON.
                    string receivedJson = Encoding.UTF8.GetString(receivedBytes);

                    // --- Bloco de "Pretty-Printing" ---
                    // O código a seguir não é estritamente necessário para a funcionalidade,
                    // mas serve para reformatar o JSON com indentação, tornando a saída
                    // no console muito mais legível para análise e demonstração.
                    using (JsonDocument doc = JsonDocument.Parse(receivedJson))
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        string formattedJson = JsonSerializer.Serialize(doc.RootElement, options);
                        
                        Console.WriteLine("-------------------------------------------");
                        Console.WriteLine($"Relatório recebido às {DateTime.Now:HH:mm:ss.fff}:");
                        Console.WriteLine(formattedJson);
                    }
                }
                catch (Exception e)
                {
                    // Captura qualquer erro que possa ocorrer durante o recebimento ou
                    // processamento do pacote.
                    Console.WriteLine($"\nERRO AO PROCESSAR PACOTE: {e.Message}");
                }
            }
        }
    }
}