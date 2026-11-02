# PrintShop

PrintShop é um sistema web em ASP.NET Core 8 criado para automatizar pedidos de impressão em papelarias.

A ideia é diminuir o atendimento manual por WhatsApp: o cliente acessa o site, envia os arquivos, escolhe as opções de impressão, revisa o valor, escolhe a forma de pagamento e acompanha o andamento do pedido pelo código.

O sistema também possui uma área administrativa para a papelaria acompanhar pedidos, autorizar pagamentos, configurar preços, configurar impressoras e consultar relatórios.

Este projeto ainda está em desenvolvimento e não deve ser usado em produção sem revisão de segurança, backup, credenciais reais e integração definitiva de pagamento.

---

## Funcionalidades

- Envio de arquivos PDF, Word, PowerPoint, JPG e PNG.
- Arquivos armazenados fora da pasta pública do site.
- Banco SQLite para pedidos e configurações.
- Contagem real de páginas dos arquivos (PDF inclusive com objetos comprimidos; Word e PowerPoint antigos pelo Office instalado no servidor).
- Cálculo por página, tipo de papel, entrega e cópias por arquivo; plastificação cobrada por folha (em frente e verso, duas páginas = uma folha).
- Escolha entre preto e branco ou colorido.
- Escolha entre frente simples ou frente e verso.
- Escolha entre retrato e paisagem.
- Edição do pedido antes da confirmação da forma de pagamento.
- Pré-visualização disponível para PDF.
- Escolha entre retirada na loja ou entrega.
- Entrega direcionada para PIX.
- Retirada com PIX ou pagamento na loja.
- Pedido só entra no painel administrativo depois que o cliente confirma a forma de pagamento.
- Busca de pedido pelo código.
- Painel administrativo com login.
- Filtros, busca e detalhes de pedidos no admin.
- Autorização manual de pagamento na loja pelo admin.
- Reimpressão, cancelamento e marcação como entregue; o agente marca o pedido como pronto ao terminar.
- Relatórios administrativos com filtro de período e exportação CSV.
- Configuração de preços e impressoras pelo admin.
- Fila persistente de impressão por arquivo.
- Agente Windows separado para executar a impressão fora do IIS.
- PIX pelo Mercado Pago (Orders API) com webhook assinado.
- HTTPS, HSTS fora de desenvolvimento, cookies seguros e cabeçalhos de segurança.
- Rate limiting por IP: global, upload, login admin, busca de pedido e webhook.

---

## Tecnologias

- ASP.NET Core 8 MVC
- Razor Views
- SQLite
- HTML, CSS e JavaScript
- IIS/Windows para hospedagem recomendada
- Data Protection do ASP.NET Core
- Rate Limiting nativo do ASP.NET Core

---

## Como executar localmente

Clone o repositório:

```powershell
git clone https://github.com/AlbinoNego/PrintShop.git
```

Entre na pasta:

```powershell
cd PrintShop
```

Restaure e compile:

```powershell
dotnet restore
dotnet build
```

Execute:

```powershell
dotnet run
```

Se o navegador reclamar do certificado HTTPS local:

```powershell
dotnet dev-certs https --trust
```

No Visual Studio, abra o projeto pelo arquivo `.csproj`. Abrir somente a pasta pode fazer o Visual Studio usar outro perfil de inicialização.

---

## Configuração

`appsettings.json` e `appsettings.Local.json` **não são versionados**. Copie `appsettings.example.json` para `appsettings.json` e coloque usuário/senha do admin, credenciais do Mercado Pago e caminhos de impressão em `appsettings.Local.json` ou em variáveis de ambiente.

Principais chaves:

```text
PrintShop:Admin:Username / PrintShop:Admin:Password   (usados só para criar o primeiro admin)
PrintShop:PrintExecutables:Pdf|Word|PowerPoint|Image
PrintShop:PrintProcessTimeoutSeconds                  (padrão 180)
MercadoPago:AccessToken / MercadoPago:WebhookSecret / MercadoPago:PixExpirationMinutes
```

Atenção: o `PrintShop.Agent` lê `appsettings.json`, `appsettings.Production.json` e variáveis de ambiente, mas não `appsettings.Local.json`.

---

## Armazenamento

Os dados gerados em execução ficam em `App_Data`:

```text
App_Data/
├── printshop.db
├── uploads/
└── keys/
```

Esses arquivos não devem ir para o GitHub.

O `.gitignore` ignora banco SQLite, arquivos enviados, PDFs gerados, chaves locais, logs, build e arquivos de ambiente.

---

## Fluxo do cliente

1. O cliente cria um novo pedido.
2. Envia os arquivos.
3. Escolhe papel, cor, lados, orientação, plastificação, cópias por arquivo e retirada/entrega.
4. Informa nome e telefone.
5. Revisa o pedido.
6. Se voltar para editar, os arquivos já enviados continuam no pedido.
7. Confirma a forma de pagamento.
8. O pedido entra no painel administrativo.
9. O cliente acompanha pelo código.

Enquanto o cliente está apenas revisando, o pedido fica como rascunho e não aparece no painel administrativo.

---

## Impressão

A impressão automática fica em:

```text
Services/PrinterService.cs
```

O comportamento atual é:

- PDF: usa o leitor PDF instalado ou o fallback do Windows.
- Word: imprime diretamente quando a orientação escolhida já é a mesma do arquivo.
- Word: quando precisa mudar orientação, gera um PDF temporário com a nova orientação e imprime esse temporário.
- PowerPoint: segue a mesma lógica do Word.
- Imagens: usa o aplicativo associado no Windows.
- Fora do Windows: simulação de impressão.

Os PDFs enviados pelo cliente não são convertidos nem alterados pelo fluxo de orientação.

As impressoras e preços são configurados em:

```text
/Admin/Settings
```

### Agente de impressão

O site apenas cria trabalhos na fila quando um pagamento é autorizado ou confirmado. O executável `PrintShop.Agent` processa um arquivo por vez, sob a conta Windows destinada à impressão. Isso evita que o IIS abra aplicativos de impressão diretamente.

Para testar o agente localmente, com o site publicado em `C:\Sites\PrintShop`:

```powershell
dotnet run --project .\PrintShop.Agent\PrintShop.Agent.csproj -- --site-root "C:\Sites\PrintShop"
```

Cada processo de impressão tem tempo limite (`PrintShop:PrintProcessTimeoutSeconds`, padrão 180 s). Se o leitor de PDF não fechar sozinho (o Acrobat costuma ficar aberto), o trabalho é marcado como falha; o SumatraPDF é recomendado.

O agente respeita a opção de impressão automática do painel administrativo. Ao concluir todos os arquivos de um pedido, ele o marca como pronto. Em caso de falha, o pedido fica como `Falha na impressão` para que o administrador possa reenviá-lo.

Em `/Admin/Printers`, cadastre cada impressora com o nome instalado no Windows e suas capacidades. O agente escolhe automaticamente uma impressora ativa e não pausada que atenda ao pedido (cor, A3 e frente e verso), priorizando a que possui menos trabalhos abertos.

---

## Área administrativa

Rotas principais:

```text
/Admin/Login
/Order/Queue
/Admin/Reports
/Admin/Settings
/Admin/Printers
```

No admin é possível:

- ver pedidos;
- filtrar por status;
- buscar por código, cliente, telefone ou arquivo;
- ver detalhes;
- autorizar pagamento;
- barrar pedido;
- marcar como entregue;
- reimprimir;
- configurar preços;
- configurar impressoras;
- cadastrar e pausar impressoras;
- acompanhar a carga das impressoras e os trabalhos individuais da fila de impressão;
- filtrar trabalhos por status e reenviar falhas;
- pausar impressão automática;
- consultar relatórios.

---

## PIX Mercado Pago

O PIX é criado pela API do Mercado Pago. O cliente recebe o QR Code e o código copia-e-cola retornados pelo provedor; a impressão só entra na fila após um webhook confirmado.

Coloque as credenciais em `appsettings.Local.json` (ignorado pelo Git) seguindo a seção `MercadoPago` de `appsettings.example.json`. Ao editar um pedido que já tem PIX gerado, o site cancela a cobrança anterior no Mercado Pago; se ela já tiver sido paga, a edição é bloqueada. Para o Checkout Transparente via Orders, o Access Token de teste do Mercado Pago começa com `APP_USR-`:

```json
{
  "MercadoPago": {
    "AccessToken": "APP_USR-seu-access-token-de-teste",
    "WebhookSecret": "sua-assinatura-secreta-do-webhook",
    "PixExpirationMinutes": 1440
  }
}
```

No painel do Mercado Pago, configure o evento **Orders** para a URL pública abaixo:

```http
POST /Pix/MercadoPagoWebhook
```

O endpoint valida a assinatura do Mercado Pago, consulta a order na API e confere identificador, valor, moeda e referência antes de confirmar o pedido.

---

## Segurança

Já existe:

- HTTPS, HSTS fora de desenvolvimento e cookies seguros;
- cabeçalhos `nosniff`, `X-Frame-Options`, `Referrer-Policy` e CSP `frame-ancestors`;
- rate limiting por IP;
- senha admin com hash PBKDF2 e usuários no banco;
- upload fora do `wwwroot`, validado pela assinatura do arquivo;
- pré-visualização com tipo fixo pela extensão (só PDF abre no navegador);
- páginas do pedido acessíveis só pela sessão que criou/buscou o pedido ou pelo admin;
- `.gitignore` para configurações locais, dados e arquivos de clientes.

Ainda precisa melhorar antes de produção:

- backup automático do SQLite e modo WAL;
- limpeza automática de rascunhos e uploads antigos;
- token antiforgery nas ações administrativas;
- auditoria de ações administrativas;
- configuração definitiva de domínio e certificado.

---

## Hospedagem recomendada

Para testes locais com impressão automática, o caminho mais simples é Windows Server em VM local ou servidor local na rede da papelaria.

Como a impressão acontece na máquina onde a aplicação roda, a VM precisa enxergar as impressoras instaladas. Em nuvem, seria necessário VPN, print server ou um agente local de impressão.

Para Windows Server:

- IIS;
- ASP.NET Core Hosting Bundle do .NET 8;
- drivers das impressoras;
- Office instalado, se for imprimir Word/PowerPoint;
- leitor PDF instalado;
- permissões de escrita em `App_Data`.

---

## Comandos úteis

Build:

```powershell
dotnet build --no-restore /p:UseAppHost=false
```

Publicação:

```powershell
dotnet publish -c Release -o C:\Sites\PrintShop
```

Git (confira o `git status` antes; configurações locais e dados já ficam fora pelo `.gitignore`):

```powershell
git status
git add -A
git commit -m "mensagem"
git push origin main
```

---

## Status

A versão atual cobre o fluxo principal de pedido, pagamento, acompanhamento, painel administrativo, relatórios e impressão automática local em Windows.

O projeto ainda precisa de validação em ambiente real de papelaria antes de qualquer uso em produção.
