# BatteryTray

Mostra a bateria dos seus dispositivos sem fio direto na barra de tarefas do Windows 11. Cada aparelho aparece como um ícone fixado, com o desenho do aparelho em cima e a porcentagem embaixo. Quando ele está carregando, o "%" vira um raio.

## O que ele detecta

| Origem | Como lê |
|---|---|
| **Logitech** (receptores LIGHTSPEED, Unifying e Bolt, ou aparelho no cabo) | HID++ 2.0 direto no aparelho (features `0x1004`, `0x1000`, `0x1001` e `0x1F20`). Não precisa do G HUB. |
| **Razer, headsets** (ex.: Kraken V3 Pro) | Eventos de bateria registrados pelo Razer Synapse 4 nos logs dele. |
| **Razer, mouses e teclados sem fio** | Relatório USB de 90 bytes, o mesmo protocolo documentado pelo projeto openrazer. |
| **Bluetooth (qualquer marca)** | A bateria que o próprio Windows conhece, a mesma de Configurações → Bluetooth. |
| **USB/HID genérico** | Campo de bateria padrão HID (`Battery Strength` ou `Remaining Capacity`). |
| **Controles XInput** | Quando o controle informa a bateria (o dongle da 8BitDo não informa). |

Dongles 2.4 GHz com protocolo próprio da marca não são suportados.

## Uso

- **Fixar na barra:** um aparelho novo aparece dentro da setinha `^` da bandeja. Arraste-o para a barra uma vez e o Windows lembra a posição.
- **Detalhes:** passe o mouse sobre o ícone para ver o nome e a porcentagem.
- **Menu (clique direito):**
  - escolher quais aparelhos aparecem na barra;
  - atualizar as leituras;
  - iniciar com o Windows;
  - verificar e instalar atualizações.

## Compilar

Basta rodar `build.cmd`. Ele usa o compilador C# do .NET Framework 4, que já vem no Windows, então não precisa instalar nenhum SDK.

Para testar sem abrir a interface, rode `BatteryTray.exe --dump`. Ele grava as leituras e prévias dos ícones na pasta `preview\`.

## Publicar uma versão

```powershell
.\release.ps1 1.1.0
```

O script atualiza `Updater.Version`, compila, faz o commit, cria a tag `v1.1.0` e envia. O GitHub Actions compila e publica o Release com o `BatteryTray.exe` e o `BatteryTray.exe.sha256`.

O app instalado verifica atualizações ao abrir e a cada 6 horas, e também pelo menu. Antes de trocar o executável, ele confere o SHA-256 do arquivo baixado.
