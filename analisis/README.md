# Backtest de FutbolLab

Scripts para medir los modelos contra los partidos guardados en `brain/database/tracker/`.

```bash
unzip futbollab_standalone.zip -d /tmp/fl
python3 analisis/backtest.py /tmp/fl/futbollab_standalone   # compara modelos (log-loss, RPS, acierto)
python3 analisis/sig.py      /tmp/fl/futbollab_standalone   # ¿son estables las "firmas" de equipo?
```

Validación walk-forward: cada partido se predice solo con partidos anteriores de su liga.
Resultados (1.083 partidos evaluados, 4 ligas, 2025-08 → 2026-05):

| modelo | log-loss | RPS | acierto |
|---|---|---|---|
| frecuencias de la liga (no saber nada) | 1.088 | 0.231 | 41.9% |
| `xg_engine.js` actual, últimos 5 | 1.177 | 0.241 | 46.6% |
| `xg_engine.js` actual, toda la temporada | 1.060 | 0.214 | 50.4% |
| normalizado por media de liga | 1.028 | 0.209 | 50.4% |
| Poisson MLE + decay + prior | 1.012 | 0.205 | 51.1% |
| + corrección Dixon-Coles | 1.011 | 0.205 | 51.0% |
