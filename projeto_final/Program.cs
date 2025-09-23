using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Linq;
using Terminal.Gui;

/// Este módulo atua como um filtro de eventos em tempo real, recebendo dados de medição de IEDs (Intelligent Electronic Devices), aplicando um conjunto de regras dinâmicas e emitindo relatórios periódicos sobre os eventos detectados.
public class Modulo4_Final
{
    #region Estruturas de Dados
    
    /// Representa uma regra de filtragem definida pelo usuário.
    public class Regra
    {
        public string? Nome { get; set; }
        public string? Parametro { get; set; }
        public string? Operador { get; set; }
        public double Valor { get; set; }
    }

    /// Representa o relatório enviado para o Módulo 3.
    public class RelatorioEventos
    {
        public DateTime Timestamp { get; set; }
        public int TotalEventos { get; set; }
        public Dictionary<int, int>? EventosPorIed { get; set; }
    }

    #endregion

    #region Variáveis de Estado e Controle de Concorrência

    // Armazena a lista de regras carregadas e ativas no sistema.
    private static List<Regra> _regrasAtivas = new();

    // Contadores para os eventos detectados.
    private static int _totalEventos = 0;
    // Dicionário thread-safe para armazenar a contagem de eventos por IED.
    private static readonly ConcurrentDictionary<int, int> _eventosPorIed = new();

    // Objeto usado para garantir acesso exclusivo (lock) ao contador _totalEventos.
    private static readonly object _lockContadorTotal = new();
    
    // Timer que dispara o envio de relatórios a cada 500ms.
    private static Timer? _senderTimer;

    // Variáveis para detecção de deadline miss. Sinalizador para controlar se a tarefa de envio de relatório já está em execução.
    private static bool _isSendingReport = false;
    // Contador de deadlines perdidas.
    private static int _relatorioDeadlinesPerdidos = 0;
    // Objeto de lock para garantir acesso seguro ao sinalizador _isSendingReport.
    private static readonly object _lockDeadline = new();

    #endregion

    #region Componentes da Interface (TUI)

    // Referências para os componentes da UI que precisam ser atualizados por outras threads.
    private static ListView? _regrasListView;
    private static TextView? _logTextView;
    private static StatusBar? _statusBar;

    #endregion

    #region Lógica de Persistência

    /// Carrega as regras do arquivo 'regras.json' ao iniciar a aplicação.
    private static void CarregarRegras()
    {
        try
        {
            if (File.Exists("regras.json"))
            {
                string jsonString = File.ReadAllText("regras.json");
                _regrasAtivas = JsonSerializer.Deserialize<List<Regra>>(jsonString) ?? new List<Regra>();
            }
        }
        catch (Exception e)
        {
            MessageBox.ErrorQuery("Erro de Configuração", $"Falha ao carregar 'regras.json': {e.Message}", "Ok");
            _regrasAtivas = new List<Regra>();
        }
    }

    /// Salva a lista de regras atual no arquivo 'regras.json'.
    private static void SalvarRegras()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(_regrasAtivas, options);
            File.WriteAllText("regras.json", jsonString);
        }
        catch (Exception e)
        {
            MessageBox.ErrorQuery("Erro ao Salvar", $"Não foi possível salvar as regras em 'regras.json': {e.Message}", "Ok");
        }
    }

    #endregion

    #region Lógica Principal 

    /// Processa um pacote de dados recebido, aplicando todas as regras ativas e registrando eventos se as condições forem atendidas.
    private static void AplicarRegras(Dictionary<string, JsonElement> dadosPacote)
    {
        // Inicia um cronômetro para medir a latência do processamento.
        var stopwatch = Stopwatch.StartNew();

        // Valida se o pacote contém a informação essencial do ID do dispositivo.
        if (!dadosPacote.TryGetValue("IedId", out var iedIdElement) || !iedIdElement.TryGetInt32(out int iedId))
        {
            return;
        }

        // Cria uma cópia da lista de regras para evitar problemas de concorrência caso a lista seja modificada pela UI enquanto este método está executando.
        var regrasParaVerificar = _regrasAtivas.ToList();

        foreach (var regra in regrasParaVerificar)
        {
            // Verifica se o parâmetro definido na regra existe no pacote de dados recebido.
            if (regra.Parametro != null && dadosPacote.TryGetValue(regra.Parametro, out var valorElement) && valorElement.TryGetDouble(out double valorRecebido))
            {
                bool eventoOcorreu = false;
                // Aplica o operador de comparação da regra.
                switch (regra.Operador)
                {
                    case ">": if (valorRecebido > regra.Valor) eventoOcorreu = true; break;
                    case "<": if (valorRecebido < regra.Valor) eventoOcorreu = true; break;
                    case "=": case "==": if (valorRecebido == regra.Valor) eventoOcorreu = true; break;
                }

                if (eventoOcorreu)
                {
                    stopwatch.Stop(); // Para o cronômetro para obter a medição de desempenho.

                    // Atualiza os contadores de forma segura para threads (thread-safe).
                    lock (_lockContadorTotal) { _totalEventos++; }
                    _eventosPorIed.AddOrUpdate(iedId, 1, (id, count) => count + 1);

                    string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | EVENTO: Regra '{regra.Nome}' acionada. ({regra.Parametro}: {valorRecebido:F4} {regra.Operador} {regra.Valor}) [Proc: {stopwatch.Elapsed.TotalMilliseconds:F4} ms]\n";

                    // CRÍTICO: Qualquer atualização da UI a partir de uma thread de background DEVE ser feita dentro de 'Application.MainLoop.Invoke' para evitar crashes.
                    Application.MainLoop.Invoke(() => {
                        if (_logTextView != null)
                        {
                            _logTextView.Text += logMessage;

                            // Lógica de auto-scroll para manter os logs mais recentes visíveis.
                            int totalLines = _logTextView.Lines;
                            int viewHeight = _logTextView.Bounds.Height;
                            if (totalLines > viewHeight)
                            {
                                _logTextView.TopRow = totalLines - viewHeight;
                            }
                            _logTextView.SetNeedsDisplay();
                        }

                        if (_statusBar?.Items.Length > 2)
                        {
                            _statusBar.Items[1].Title = $"Total Eventos: {_totalEventos}";
                            _statusBar.SetNeedsDisplay();
                        }
                    });
                }
            }
        }
    }

    /// Executado em uma thread de background, este método ouve continuamente a rede por pacotes UDP na porta especificada.
    private static void ListenerUdp()
    {
        int listenPort = 54321;
        using var udpClient = new UdpClient(listenPort);
        var remoteEP = new IPEndPoint(IPAddress.Any, listenPort);

        Application.MainLoop.Invoke(() => {
            _logTextView?.InsertText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | Ouvindo pacotes UDP na porta {listenPort}...\n");
        });

        try
        {
            // Loop infinito para receber pacotes continuamente.
            while (true)
            {
                // A chamada .Receive() é bloqueante, ela pausa a thread até um pacote chegar.
                byte[] bytes = udpClient.Receive(ref remoteEP);
                string receivedJson = Encoding.UTF8.GetString(bytes);

                try
                {
                    var dadosPacote = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(receivedJson);
                    if (dadosPacote != null)
                    {
                        // Envia os dados para o motor de regras processar.
                        AplicarRegras(dadosPacote);
                    }
                }
                catch (JsonException) { /* Ignora JSON mal formatado */ }
            }
        }
        catch (Exception e)
        {
            Application.MainLoop.Invoke(() => {
                _logTextView?.InsertText($"\n{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | ERRO no Listener UDP: {e.Message}\n");
            });
        }
    }

    /// Executado periodicamente por um Timer, este método envia um relatório de status
    /// e implementa a lógica de detecção de deadlines perdidos.

    private static void SenderRelatorio(object? state)
    {
        // Usa um lock para garantir que a verificação e alteração do sinalizador _isSendingReport seja uma operação atômica, evitando condições de corrida.
        lock (_lockDeadline)
        {
            if (_isSendingReport)
            {
                // DEADLINE MISS: Se a função for chamada enquanto a execução anterior ainda não terminou, um deadline foi perdido.
                _relatorioDeadlinesPerdidos++;
                Application.MainLoop.Invoke(() => {
                    if (_statusBar?.Items.Length > 2)
                    {
                        _statusBar.Items[2].Title = $"Deadlines Perdidos: {_relatorioDeadlinesPerdidos}";
                        _statusBar.SetNeedsDisplay();
                    }
                });
                return; // Aborta a execução atual para não sobrecarregar a rede.
            }
            // Sinaliza que a tarefa de envio começou.
            _isSendingReport = true;
        }

        try
        {
            // Linha de teste para forçar um deadline perdido(600ms > 500ms).

            using var udpClient = new UdpClient();
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, 12000);
            var relatorio = new RelatorioEventos();

            // Acessa os contadores de forma segura para criar um relatório consistente.
            lock (_lockContadorTotal)
            {
                relatorio.TotalEventos = _totalEventos;
            }
            relatorio.EventosPorIed = new Dictionary<int, int>(_eventosPorIed);
            relatorio.Timestamp = DateTime.Now;

            string jsonString = JsonSerializer.Serialize(relatorio);
            byte[] data = Encoding.UTF8.GetBytes(jsonString);
            udpClient.Send(data, data.Length, broadcastEndpoint);
        }
        finally
        {
            // O bloco 'finally' garante que o sinalizador será resetado para 'false',
            // mesmo que ocorra um erro durante o envio, evitando um deadlock.
            lock (_lockDeadline)
            {
                _isSendingReport = false;
            }
        }
    }

    #endregion

    #region Diálogos da UI

    /// Cria e exibe o diálogo para adicionar uma nova regra.
    private static void AdicionarNovaRegraDialog()
    {
        var dialog = new Dialog("Adicionar Nova Regra", 60, 10);
        var lblNome = new Label("Nome:") { X = 1, Y = 1 };
        var txtNome = new TextField("") { X = 15, Y = 1, Width = 40 };
        var lblParam = new Label("Parâmetro:") { X = 1, Y = 2 };
        var txtParam = new TextField("") { X = 15, Y = 2, Width = 40 };
        var lblOp = new Label("Operador:") { X = 1, Y = 3 };
        var txtOp = new TextField("") { X = 15, Y = 3, Width = 40 };
        var lblValor = new Label("Valor:") { X = 1, Y = 4 };
        var txtValor = new TextField("") { X = 15, Y = 4, Width = 40 };

        var btnOk = new Button("Ok", is_default: true);
        btnOk.Clicked += () => {
            if (string.IsNullOrWhiteSpace(txtNome.Text.ToString()) ||
                string.IsNullOrWhiteSpace(txtParam.Text.ToString()) ||
                string.IsNullOrWhiteSpace(txtOp.Text.ToString()) ||
                !double.TryParse(txtValor.Text.ToString(), out double valor))
            {
                MessageBox.ErrorQuery("Dados Inválidos", "Preencha todos os campos corretamente.", "Ok");
                return;
            }

            var novaRegra = new Regra
            {
                Nome = txtNome.Text.ToString(),
                Parametro = txtParam.Text.ToString(),
                Operador = txtOp.Text.ToString(),
                Valor = valor
            };

            _regrasAtivas.Add(novaRegra);
            SalvarRegras();
            _regrasListView?.SetSource(_regrasAtivas.Select(r => r.Nome ?? "").ToList());
            Application.RequestStop(dialog);
        };
        
        var btnCancelar = new Button("Cancelar");
        btnCancelar.Clicked += () => { Application.RequestStop(dialog); };

        dialog.Add(lblNome, txtNome, lblParam, txtParam, lblOp, txtOp, lblValor, txtValor);
        dialog.AddButton(btnOk);
        dialog.AddButton(btnCancelar);
        Application.Run(dialog);
    }

    /// Cria e exibe o diálogo para editar uma regra selecionada.
    private static void EditarRegraSelecionadaDialog()
    {
        if (_regrasListView == null) return;
        int selectedIndex = _regrasListView.SelectedItem;

        if (selectedIndex < 0 || selectedIndex >= _regrasAtivas.Count)
        {
            MessageBox.ErrorQuery("Nenhuma Regra Selecionada", "Por favor, selecione uma regra na lista para editar.", "Ok");
            return;
        }

        var regraParaEditar = _regrasAtivas[selectedIndex];
        var dialog = new Dialog("Editar Regra", 60, 10);
        
        // Pré-ocupa os campos com os dados da regra existente.
        var lblNome = new Label("Nome:") { X = 1, Y = 1 };
        var txtNome = new TextField(regraParaEditar.Nome) { X = 15, Y = 1, Width = 40 };
        var lblParam = new Label("Parâmetro:") { X = 1, Y = 2 };
        var txtParam = new TextField(regraParaEditar.Parametro) { X = 15, Y = 2, Width = 40 };
        var lblOp = new Label("Operador:") { X = 1, Y = 3 };
        var txtOp = new TextField(regraParaEditar.Operador) { X = 15, Y = 3, Width = 40 };
        var lblValor = new Label("Valor:") { X = 1, Y = 4 };
        var txtValor = new TextField(regraParaEditar.Valor.ToString()) { X = 15, Y = 4, Width = 40 };

        var btnOk = new Button("Salvar", is_default: true);
        btnOk.Clicked += () => {
            if (string.IsNullOrWhiteSpace(txtNome.Text.ToString()) ||
                string.IsNullOrWhiteSpace(txtParam.Text.ToString()) ||
                string.IsNullOrWhiteSpace(txtOp.Text.ToString()) ||
                !double.TryParse(txtValor.Text.ToString(), out double valor))
            {
                MessageBox.ErrorQuery("Dados Inválidos", "Preencha todos os campos corretamente.", "Ok");
                return;
            }
            
            // Atualiza o objeto da regra existente em vez de criar um novo.
            regraParaEditar.Nome = txtNome.Text.ToString();
            regraParaEditar.Parametro = txtParam.Text.ToString();
            regraParaEditar.Operador = txtOp.Text.ToString();
            regraParaEditar.Valor = valor;

            SalvarRegras();
            _regrasListView?.SetSource(_regrasAtivas.Select(r => r.Nome ?? "").ToList());
            _regrasListView.SelectedItem = selectedIndex;
            Application.RequestStop(dialog);
        };
        
        var btnCancelar = new Button("Cancelar");
        btnCancelar.Clicked += () => { Application.RequestStop(dialog); };

        dialog.Add(lblNome, txtNome, lblParam, txtParam, lblOp, txtOp, lblValor, txtValor);
        dialog.AddButton(btnOk);
        dialog.AddButton(btnCancelar);
        Application.Run(dialog);
    }

    /// Remove a regra atualmente selecionada na lista, após confirmação.
    private static void RemoverRegraSelecionada()
    {
        if (_regrasListView == null) return;
        int selectedIndex = _regrasListView.SelectedItem;

        if (selectedIndex < 0 || selectedIndex >= _regrasAtivas.Count)
        {
            MessageBox.ErrorQuery("Nenhuma Regra Selecionada", "Por favor, selecione uma regra na lista para remover.", "Ok");
            return;
        }

        var regraParaRemover = _regrasAtivas[selectedIndex];
        int confirmacao = MessageBox.Query("Confirmar Remoção", $"Você tem certeza que deseja remover a regra '{regraParaRemover.Nome}'?", "Não", "Sim");

        if (confirmacao == 1)
        {
            _regrasAtivas.RemoveAt(selectedIndex);
            SalvarRegras();
            _regrasListView.SetSource(_regrasAtivas.Select(r => r.Nome ?? "").ToList());
            if (_regrasAtivas.Count > 0)
            {
                _regrasListView.SelectedItem = 0;
            }
            _regrasListView.SetNeedsDisplay();
        }
    }

    #endregion

    #region Ciclo de Vida da Aplicação

    /// Inicia os componentes de backend (threads de rede e timers) após a interface gráfica ter sido carregada.
    private static void IniciarBackend()
    {
        CarregarRegras();
        _regrasListView?.SetSource(_regrasAtivas.Select(r => r.Nome ?? "Regra sem nome").ToList());

        // Inicia a thread de escuta da rede em background.
        var listenerThread = new Thread(ListenerUdp) { IsBackground = true };
        listenerThread.Start();
        
        // Inicia o timer para envio de relatórios.
        _senderTimer = new Timer(SenderRelatorio, null, 1000, 500);
    }

    /// Ponto de entrada principal da aplicação.
    public static void Main(string[] args)
    {
        // Inicializa a aplicação de console.
        Application.Init();
        var top = Application.Top;

        // Montagem da Interface Gráfica (TUI)
        var menu = new MenuBar(new MenuBarItem[] {
            new MenuBarItem("_Arquivo", new MenuItem [] {
                new MenuItem ("_Sair", "", () => Application.RequestStop(), null, null, Key.CtrlMask | Key.Q)
            }),
            new MenuBarItem("_Regras", new MenuItem [] {
                new MenuItem ("_Adicionar Nova...", "", AdicionarNovaRegraDialog),
                new MenuItem ("_Editar Selecionada...", "", EditarRegraSelecionadaDialog),
                new MenuItem ("_Remover Selecionada...", "", RemoverRegraSelecionada)
            })
        });

        var mainWindow = new Window("Módulo 4: Filtro de Eventos") { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill() - 1 };
        var regrasWindow = new FrameView("Regras Ativas") { X = 0, Y = 0, Width = 35, Height = Dim.Fill() };
        _regrasListView = new ListView() { Width = Dim.Fill(), Height = Dim.Fill() };
        regrasWindow.Add(_regrasListView);

        var logWindow = new FrameView("Log de Eventos") { X = Pos.Right(regrasWindow), Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        
        _logTextView = new TextView() { 
            Width = Dim.Fill(), 
            Height = Dim.Fill(), 
            ReadOnly = true, 
            CanFocus = false, 
            WordWrap = true 
        };
        logWindow.Add(_logTextView);

        _statusBar = new StatusBar(new StatusItem[] {
            new(Key.CtrlMask | Key.Q, "~^Q~ Sair", () => Application.RequestStop()),
            new(Key.Null, "Total Eventos: 0", null),
            new(Key.Null, "Deadlines Perdidos: 0", null)
        });

        mainWindow.Add(regrasWindow, logWindow);
        top.Add(menu, mainWindow, _statusBar);

        // Associa o método IniciarBackend ao evento 'Loaded', garantindo que o backend só comece a rodar depois que a UI estiver pronta.
        top.Loaded += IniciarBackend;

        // Inicia o loop principal da aplicação, que processa eventos e desenha a tela.
        Application.Run();

        // Limpeza de recursos ao fechar a aplicação.
        _senderTimer?.Dispose();
        Application.Shutdown();
    }

    #endregion
}