# Status do Projeto - PrintShop

Última atualização: 02/11/2026 — correções de segurança, plastificação por folha, contagem de páginas pelo Office, painel e relatórios revisados.

## Objetivo

O PrintShop é um sistema ASP.NET Core 8 MVC para papelarias receberem pedidos de impressão online.

O cliente envia arquivos, configura impressão, escolhe retirada ou entrega, revisa o valor, paga via PIX ou na loja, e acompanha o pedido pelo código.

O admin acompanha pedidos, confirma pagamentos, gerencia impressão, configura preços/impressoras e consulta relatórios.

## Estado atual

Funcionalidades implementadas:

- Upload de PDF, Word, PowerPoint, JPG e PNG.
- Arquivos salvos fora do `wwwroot`, em `App_Data/uploads`.
- Pedidos e configurações salvos em SQLite, em `App_Data/printshop.db`.
- Contagem real de páginas: PDF (inclusive object streams), DOC/DOCX e PPT pelo Office do servidor, PPTX pelo número de slides.
- Cálculo de preço por página, cópias por arquivo, tipo de papel e entrega; plastificação por folha.
- Orientação retrato/paisagem.
- Pré-visualização disponível para PDF.
- Edição de pedido antes do pagamento sem perder arquivos enviados.
- Remoção de arquivos durante edição do pedido.
- Busca de pedido pelo código.
- Telefone obrigatório para criar pedido.
- Retirada ou entrega.
- Entrega vai direto para PIX.
- Retirada permite PIX ou pagamento na loja.
- Pedido fica como rascunho durante a revisão e só entra no painel admin após confirmação da forma de pagamento.
- Painel admin com login persistido em SQLite e senha protegida por hash PBKDF2.
- Admin pode autorizar pagamento na loja, barrar pedido, marcar como entregue e reimprimir; o agente marca como pronto.
- Painel admin tem busca, filtro por status, detalhes com acesso aos arquivos, contadores do dia e aviso de pedidos novos.
- Relatórios administrativos com filtro de período e exportação CSV.
- Tela admin `/Admin/Settings` para preços, impressoras e pausa da impressão automática.
- Impressão automática funcionando em testes para PDF, PPT/PPTX, DOC/DOCX e imagens.
- Word/PowerPoint imprimem direto quando a orientação não muda; quando muda, o sistema gera PDF temporário para aplicar a orientação.
- HTTPS configurado.
- Cookies de sessão seguros.
- HSTS fora de desenvolvimento.
- Rate limiting global, para upload, webhook PIX e tentativas de login administrativo.
- PIX Mercado Pago integrado pela Orders API, com QR Code dinâmico, idempotência e webhook assinado em `/Pix/MercadoPagoWebhook`.
- Cadastro de impressoras, distribuição automática por capacidade/carga e fila persistente por arquivo em `/Admin/Printers`.
- Projeto `PrintShop.Agent`, que processa a fila fora do IIS.

## GitHub

Repositório:

```text
https://github.com/AlbinoNego/PrintShop
```

Branch principal:

```text
main
```

`main` e `feature/print-agent-queue` estão no mesmo ponto. O histórico foi reescrito em 02/11/2026 para remover dados pessoais; clones antigos devem usar `git fetch` + `git reset --hard origin/main`.

## Rotas principais

Cliente:

```text
/
/Order/New
/Order/Review/{id}
/Order/Payment/{id}
/Order/PixPayment/{id}
/Order/Success/{id}
/Order/MyOrders
```

Admin:

```text
/Admin/Login
/Order/Queue
/Admin/Reports
/Admin/Settings
```

Webhook:

```text
POST /Pix/MercadoPagoWebhook
```

## Arquivos importantes

Controllers:

```text
Controllers/OrderController.cs
Controllers/AdminController.cs
Controllers/PixController.cs
```

Services:

```text
Services/OrderQueueService.cs
Services/FileStorageService.cs
Services/PageCountService.cs
Services/PricingService.cs
Services/PrinterService.cs
Services/AdminSettingsService.cs
Services/MercadoPagoPixService.cs
Services/PrinterRegistryService.cs
```

Models:

```text
Models/PrintOrder.cs
Models/AdminSettings.cs
Models/AdminReportViewModel.cs
```

Views principais:

```text
Views/Order/New.cshtml
Views/Order/Review.cshtml
Views/Order/Payment.cshtml
Views/Order/PixPayment.cshtml
Views/Order/Success.cshtml
Views/Order/Queue.cshtml
Views/Admin/Settings.cshtml
Views/Admin/Reports.cshtml
```

## Decisões importantes

- Uploads não ficam em `wwwroot`.
- SQLite é usado como banco local.
- Cliente não tem login; acompanha pedido por código.
- O primeiro usuário admin é criado com a configuração local; a senha é armazenada em hash no banco e não pode ser alterada pelo painel.
- Pedido só pode ser editado antes da confirmação do pagamento.
- Entrega obriga pagamento via PIX.
- Retirada pode usar PIX ou pagamento presencial.
- PIX Mercado Pago gera cobrança dinâmica, verifica valor, moeda e referência no webhook antes de liberar a impressão.
- Impressão usa aplicativos instalados no Windows e ações de impressão do sistema.
- PDFs enviados pelo cliente não são alterados pelo fluxo de orientação.
- Configuração de impressoras fica somente na área admin.
- Preços são editáveis no admin e persistidos em SQLite.

## Configurações sensíveis

O `appsettings.json` foi deixado com valores genéricos para GitHub.

Antes de produção, configurar:

```text
PrintShop:Admin:Username
PrintShop:Admin:Password
```

Para PIX Mercado Pago, usar `appsettings.Local.json` ou variáveis de ambiente:

```text
MercadoPago:AccessToken
MercadoPago:WebhookSecret
MercadoPago:PixExpirationMinutes
```

Recomendado usar variáveis de ambiente ou outro provedor seguro de configuração em produção.

## Rate limiting atual

Configurado em `Program.cs`.

Todos os limites são por IP:

- Global: 120 requisições por minuto.
- Upload: 20 envios a cada 10 minutos.
- Login admin: 10 tentativas a cada 15 minutos.
- Busca de pedido por código: 15 a cada 10 minutos.
- Webhook PIX: 60 chamadas por minuto.

## HTTPS

Configurado em `Program.cs`.

Inclui:

- redirecionamento HTTPS;
- HSTS fora de desenvolvimento;
- cookies seguros;
- suporte a `X-Forwarded-For` e `X-Forwarded-Proto`.

Perfil local usa HTTPS em `Properties/launchSettings.json`.

## Impressão

Testado localmente:

- PDF: funcionando.
- PPT/PPTX: funcionando.
- DOC/DOCX: funcionando.
- Imagens: funcionando.

Observação:

- A configuração de impressoras é feita em `/Admin/Settings`.
- Quando uma impressora específica é definida, o sistema troca temporariamente a impressora padrão do Windows para imprimir e depois restaura a anterior.
- Para testes com VM local, a VM precisa enxergar as impressoras instaladas na rede/localmente.
- O site não imprime mais diretamente após pagamento: ele cria trabalhos no banco e aguarda o `PrintShop.Agent`.

## Pendências recomendadas

Prioridades futuras:

1. Trocar a senha de admin (a atual é curta).
2. Trocar as referências SQLite com caminho absoluto no `.csproj` por `PackageReference`; ativar WAL e foreign keys.
3. Fazer o agente ler `appsettings.Local.json`; criar a página `/Home/Error`.
4. Transformar o agente em serviço da conta Windows `PrintShopPrint` na VM.
5. Backup e limpeza automática de rascunhos/arquivos; auditoria de ações administrativas.
6. Configuração real de domínio e certificado HTTPS em produção.
7. Testes automatizados (preço, estados, webhook, fila).

## Comandos úteis

Build:

```powershell
dotnet build --no-restore /p:UseAppHost=false
```

Executar:

```powershell
dotnet run
```

Confiar no certificado local:

```powershell
dotnet dev-certs https --trust
```

Git:

```powershell
git status
git add -A
git commit -m "mensagem"
git push
```
