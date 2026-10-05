# PrintShop.Agent

Agente local responsavel por retirar trabalhos da fila SQLite e executar a impressao no Windows.

## Execucao local

Com o site publicado em `C:\Sites\PrintShop`:

```powershell
dotnet run --project .\PrintShop.Agent\PrintShop.Agent.csproj -- --site-root "C:\Sites\PrintShop"
```

O agente usa as configuracoes de impressora salvas pelo painel administrativo do PrintShop. Ele deve ser executado pela conta Windows dedicada a impressao, e nao pelo IIS.
