namespace Aegis.Resilience.Extensions.Dashboard;

/// <summary>Panonun tek sayfalık, bağımlılıksız HTML/JS arayüzü.</summary>
internal static class DashboardPage
{
    public const string Html = """
        <!DOCTYPE html>
        <html lang="tr">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Aegis Resilience Dashboard</title>
            <style>
                :root {
                    --bg: #0d1117;
                    --card-bg: #161b22;
                    --border: #30363d;
                    --text: #c9d1d9;
                    --text-heading: #f0f6fc;
                    --primary: #58a6ff;
                    --success: #2ea043;
                    --danger: #f85149;
                    --warning: #d29922;
                }
                * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }
                body { background-color: var(--bg); color: var(--text); padding: 24px; min-height: 100vh; }
                .header { display: flex; flex-wrap: wrap; gap: 8px 16px; justify-content: space-between; align-items: center; margin-bottom: 24px; border-bottom: 1px solid var(--border); padding-bottom: 16px; }
                .title { font-size: 24px; font-weight: 700; color: var(--text-heading); display: flex; align-items: center; gap: 12px; }
                .badge { padding: 4px 10px; border-radius: 12px; font-size: 12px; font-weight: 600; }
                .badge-primary { background: rgba(88, 166, 255, 0.15); color: var(--primary); border: 1px solid var(--primary); }
                .badge-success { background: rgba(46, 160, 67, 0.15); color: var(--success); border: 1px solid var(--success); }
                .badge-danger { background: rgba(248, 81, 73, 0.15); color: var(--danger); border: 1px solid var(--danger); }
                .badge-warning { background: rgba(210, 153, 34, 0.15); color: var(--warning); border: 1px solid var(--warning); }
                
                .stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(200px, 100%), 1fr)); gap: 16px; margin-bottom: 24px; }
                .stat-card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 8px; padding: 16px; }
                .stat-value { font-size: 28px; font-weight: 700; color: var(--text-heading); margin-top: 8px; }
                
                .pipeline-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(340px, 100%), 1fr)); gap: 16px; }
                .pipeline-card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 8px; padding: 20px; transition: transform 0.2s; }
                .pipeline-card:hover { transform: translateY(-2px); }
                .pipeline-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; }
                .pipeline-name { font-size: 18px; font-weight: 600; color: var(--text-heading); }
                
                .strategy-list { display: flex; flex-direction: column; gap: 8px; margin-bottom: 16px; }
                .strategy-item { display: flex; gap: 8px; justify-content: space-between; align-items: center; background: rgba(255, 255, 255, 0.03); padding: 8px 12px; border-radius: 6px; }
                
                .btn-group { display: flex; flex-wrap: wrap; gap: 8px; border-top: 1px solid var(--border); padding-top: 12px; }
                .btn { padding: 6px 12px; border-radius: 6px; font-size: 12px; font-weight: 600; cursor: pointer; border: 1px solid transparent; transition: background 0.2s; }
                .btn-danger { background: rgba(248, 81, 73, 0.2); color: var(--danger); border-color: rgba(248, 81, 73, 0.4); }
                .btn-danger:hover { background: var(--danger); color: white; }
                .btn-success { background: rgba(46, 160, 67, 0.2); color: var(--success); border-color: rgba(46, 160, 67, 0.4); }
                .btn-success:hover { background: var(--success); color: white; }

                .toast { position: fixed; bottom: 24px; right: 24px; background: #1f6feb; color: white; padding: 12px 20px; border-radius: 8px; font-size: 14px; font-weight: 500; display: none; box-shadow: 0 4px 12px rgba(0,0,0,0.4); z-index: 1000; }
                
                @media (max-width: 600px) { body { padding: 16px; } .title { font-size: 20px; } }
        .pulse { width: 10px; height: 10px; border-radius: 50%; display: inline-block; margin-right: 6px; }
                .pulse-green { background: var(--success); box-shadow: 0 0 8px var(--success); }
                .pulse-red { background: var(--danger); box-shadow: 0 0 8px var(--danger); animation: blink 1s infinite; }
                @keyframes blink { 50% { opacity: 0.3; } }
            </style>
        </head>
        <body>
            <div id="toast" class="toast"></div>

            <div class="header">
                <div class="title">
                    🛡️ Aegis Resilience Dashboard
                    <span class="badge badge-primary">.NET 10 / C# 14</span>
                </div>
                <div id="last-update" style="font-size: 13px; color: #8b949e;">Canlı Güncelleniyor...</div>
            </div>

            <div class="stats-grid">
                <div class="stat-card">
                    <div>Kayıtlı Boru Hattı (Pipelines)</div>
                    <div id="stat-total" class="stat-value">0</div>
                </div>
                <div class="stat-card">
                    <div>Sağlıklı Hatlar</div>
                    <div id="stat-healthy" class="stat-value" style="color: var(--success);">0</div>
                </div>
                <div class="stat-card">
                    <div>Açık Devreler (Open Circuits)</div>
                    <div id="stat-open" class="stat-value" style="color: var(--danger);">0</div>
                </div>
            </div>

            <h3 style="color: var(--text-heading); margin-bottom: 16px;">Aktif Dayanıklılık Boru Hatları</h3>
            <div id="pipeline-container" class="pipeline-grid">
                <div style="color: #8b949e;">Yükleniyor...</div>
            </div>

            <script>
                // Boru hattı adları host/route gibi dış kaynaklardan gelebildiği için
                // DOM'a yazılan tüm metinler kaçışlanır (XSS koruması - AEGIS-120).
                function escapeHtml(value) {
                    return String(value ?? '').replace(/[&<>"']/g, c => ({
                        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
                    })[c]);
                }

                function showToast(msg) {
                    const toast = document.getElementById('toast');
                    toast.textContent = String(msg ?? '');
                    toast.style.display = 'block';
                    setTimeout(() => { toast.style.display = 'none'; }, 3000);
                }

                async function triggerAction(pipelineName, action) {
                    try {
                        const basePath = window.location.pathname.replace(/\/$/, '');
                        const res = await fetch(`${basePath}/circuits/${encodeURIComponent(pipelineName)}/${action}`, {
                            method: 'POST',
                            headers: { 'X-Aegis-Action': 'true' }
                        });
                        const result = await res.json();
                        showToast(result.message);
                        refreshData();
                    } catch (e) {
                        showToast("İşlem gerçekleştirilemedi!");
                    }
                }

                async function refreshData() {
                    try {
                        const basePath = window.location.pathname.replace(/\/$/, '');
                        const res = await fetch(`${basePath}/status`);
                        const data = await res.json();
                        
                        document.getElementById('stat-total').textContent = data.totalPipelines;
                        let healthyCount = 0;
                        let openCount = 0;

                        const container = document.getElementById('pipeline-container');
                        container.innerHTML = '';

                        data.pipelines.forEach(p => {
                            if (p.status === 'Healthy') healthyCount++;
                            else openCount++;

                            const card = document.createElement('div');
                            card.className = 'pipeline-card';

                            let strategiesHtml = '';
                            p.strategies.forEach(s => {
                                let badgeHtml = '';
                                if (s.circuitState) {
                                    if (s.circuitState === 'Closed') {
                                        badgeHtml = `<span class="badge badge-success"><span class="pulse pulse-green"></span>KAPALI</span>`;
                                    } else if (s.circuitState === 'HalfOpen') {
                                        badgeHtml = `<span class="badge badge-warning">YARI AÇIK</span>`;
                                    } else if (s.circuitState === 'Isolated') {
                                        badgeHtml = `<span class="badge badge-primary">İZOLE (bakım)</span>`;
                                    } else {
                                        badgeHtml = `<span class="badge badge-danger"><span class="pulse pulse-red"></span>AÇIK</span>`;
                                    }
                                } else {
                                    badgeHtml = `<span style="font-size:12px; color:#8b949e;">Aktif</span>`;
                                }

                                strategiesHtml += `
                                    <div class="strategy-item">
                                        <span>⚙️ ${escapeHtml(s.strategyName)}</span>
                                        ${badgeHtml}
                                    </div>
                                `;
                            });

                            let actionButtonsHtml = '';
                            if (p.hasCircuitBreaker) {
                                actionButtonsHtml = `
                                    <div class="btn-group">
                                        <button data-action="isolate" class="btn btn-danger">🔴 Manuel Kes (Isolate)</button>
                                        <button data-action="reset" class="btn btn-success">🟢 Sıfırla (Reset)</button>
                                    </div>
                                `;
                            }

                            card.innerHTML = `
                                <div class="pipeline-header">
                                    <span class="pipeline-name">${escapeHtml(p.pipelineName)}</span>
                                    <span class="badge ${p.status === 'Healthy' ? 'badge-success' : 'badge-danger'}">${escapeHtml(p.status)}</span>
                                </div>
                                <div class="strategy-list">
                                    ${strategiesHtml}
                                </div>
                                ${actionButtonsHtml}
                            `;

                            // Olay dinleyicileri: Boru hattı adı DOM'a kod olarak değil, kapanış (closure) ile taşınır
                            card.querySelectorAll('button[data-action]').forEach(btn => {
                                btn.addEventListener('click', () => triggerAction(p.pipelineName, btn.dataset.action));
                            });

                            container.appendChild(card);
                        });

                        document.getElementById('stat-healthy').textContent = healthyCount;
                        document.getElementById('stat-open').textContent = openCount;
                        document.getElementById('last-update').textContent = 'Son Güncelleme: ' + new Date().toLocaleTimeString();
                    } catch (e) {
                        console.error("Dashboard güncelleme hatası:", e);
                    }
                }

                refreshData();
                setInterval(refreshData, 3000);
            </script>
        </body>
        </html>
        """;
}
