/* =====================================================================
   AIVideo · 原型交互脚本
   —— 仅用于原型演示：切换、筛选、弹层、提示。真实数据由后端接入。
   ===================================================================== */
(function () {
  'use strict';

  /* ---------------- Toast ---------------- */
  let toastTimer = null;
  window.toast = function (msg) {
    let el = document.querySelector('.toast');
    if (!el) {
      el = document.createElement('div');
      el.className = 'toast';
      el.innerHTML = '<span class="dot"></span><span class="txt"></span>';
      document.body.appendChild(el);
    }
    el.querySelector('.txt').textContent = msg;
    el.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove('show'), 2200);
  };

  /* ---------------- 通用：分段控件 / 胶囊组 ---------------- */
  function bindGroup(container) {
    const btns = container.querySelectorAll('button');
    btns.forEach((b) => {
      b.addEventListener('click', () => {
        if (container.hasAttribute('data-multi')) {
          b.classList.toggle('on');
        } else {
          btns.forEach((x) => x.classList.remove('on'));
          b.classList.add('on');
        }
        const evt = new CustomEvent('seg:change', {
          detail: { value: b.dataset.value, group: container.dataset.group || '' },
          bubbles: true,
        });
        container.dispatchEvent(evt);
      });
    });
  }
  document.querySelectorAll('[data-seg]').forEach(bindGroup);

  /* ---------------- 侧边栏：导航 / 会话选中 ---------------- */
  document.querySelectorAll('.nav-item[data-nav]').forEach((item) => {
    item.addEventListener('click', () => {
      document.querySelectorAll('.nav-item[data-nav]').forEach((x) => x.classList.remove('on'));
      item.classList.add('on');
    });
  });

  document.querySelectorAll('.conv').forEach((c) => {
    c.addEventListener('click', (e) => {
      if (e.target.closest('.more')) return;
      document.querySelectorAll('.conv').forEach((x) => x.classList.remove('on'));
      c.classList.add('on');
    });
  });

  /* ---------------- 侧边栏折叠 ---------------- */
  document.querySelectorAll('[data-collapse]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const shell = btn.closest('.shell');
      if (shell) shell.classList.toggle('mini');
    });
  });

  /* ---------------- 弹层 ---------------- */
  window.openModal = function (id) {
    const m = document.getElementById(id);
    if (m) m.classList.add('show');
  };
  window.closeModal = function (id) {
    const m = document.getElementById(id);
    if (m) m.classList.remove('show');
  };
  document.querySelectorAll('[data-modal-open]').forEach((b) => {
    b.addEventListener('click', () => window.openModal(b.dataset.modalOpen));
  });
  document.querySelectorAll('[data-modal-close]').forEach((b) => {
    b.addEventListener('click', () => {
      const m = b.closest('.mask');
      if (m) m.classList.remove('show');
    });
  });
  document.querySelectorAll('.mask').forEach((m) => {
    m.addEventListener('click', (e) => {
      if (e.target === m) m.classList.remove('show');
    });
  });
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') document.querySelectorAll('.mask.show').forEach((m) => m.classList.remove('show'));
  });

  /* ---------------- 输入框自适应高度 ---------------- */
  document.querySelectorAll('textarea[data-auto]').forEach((ta) => {
    const fit = () => {
      ta.style.height = 'auto';
      ta.style.height = Math.min(ta.scrollHeight, 180) + 'px';
    };
    ta.addEventListener('input', fit);
    fit();
  });

  /* ---------------- 主窗口：图片 / 视频模式切换 ---------------- */
  const modeSeg = document.getElementById('modeSeg');
  if (modeSeg) {
    const apply = (mode) => {
      document.querySelectorAll('[data-only]').forEach((el) => {
        el.style.display = el.dataset.only === mode ? '' : 'none';
      });
      const ta = document.getElementById('promptInput');
      if (ta) {
        ta.placeholder =
          mode === 'video'
            ? '描述你想要的视频画面与运镜，例如：一杯冰镇汽水倒入玻璃杯，气泡翻涌，慢镜头推近…'
            : '描述你想要的画面，例如：极简日式风格的护肤品海报，柔和自然光，留白构图…';
      }
      document.querySelectorAll('[data-model-for]').forEach((el) => {
        el.style.display = el.dataset.modelFor === mode ? '' : 'none';
      });
      const hint = document.getElementById('sendHint');
      if (hint) hint.textContent = mode === 'video' ? '开始生成视频' : '开始生成图片';
    };
    apply(modeSeg.querySelector('button.on')?.dataset.value || 'image');
    modeSeg.addEventListener('seg:change', (e) => apply(e.detail.value));
  }

  /* ---------------- 我的作品：网格 / 列表 ---------------- */
  const viewSeg = document.getElementById('viewSeg');
  const worksGrid = document.getElementById('worksGrid');
  if (viewSeg && worksGrid) {
    viewSeg.addEventListener('seg:change', (e) => {
      worksGrid.classList.toggle('list', e.detail.value === 'list');
    });
  }

  /* ---------------- 我的作品：筛选（演示用前端过滤） ---------------- */
  const kindFilter = document.getElementById('kindFilter');
  if (kindFilter && worksGrid) {
    kindFilter.addEventListener('seg:change', (e) => {
      const v = e.detail.value;
      let n = 0;
      worksGrid.querySelectorAll('.work').forEach((w) => {
        const show = v === 'all' || w.dataset.kind === v;
        w.style.display = show ? '' : 'none';
        if (show) n++;
      });
      toast(n ? '已筛选：' + (v === 'all' ? '全部作品' : v === 'image' ? '图片作品' : '视频作品') + '（' + n + '）' : '该分类下暂无作品');
    });
  }

  /* ---------------- 模型配置：密钥显隐 ---------------- */
  document.querySelectorAll('[data-key-toggle]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const input = document.getElementById(btn.dataset.keyToggle);
      if (!input) return;
      const hidden = input.type === 'password';
      input.type = hidden ? 'text' : 'password';
      btn.classList.toggle('on', hidden);
      btn.setAttribute('title', hidden ? '隐藏密钥' : '显示密钥');
    });
  });

  /* ---------------- 模型配置：标签页 ---------------- */
  document.querySelectorAll('.model-tabs').forEach((tabs) => {
    const btns = tabs.querySelectorAll('button');
    btns.forEach((b) => {
      b.addEventListener('click', () => {
        btns.forEach((x) => x.classList.remove('on'));
        b.classList.add('on');
        const target = b.dataset.tab;
        document.querySelectorAll('[data-panel]').forEach((p) => {
          p.style.display = p.dataset.panel === target ? '' : 'none';
        });
      });
    });
  });

  /* ---------------- 连接测试（演示） ---------------- */
  document.querySelectorAll('[data-test]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const old = btn.innerHTML;
      btn.classList.add('is-disabled');
      btn.innerHTML = '<span class="spinner"></span>测试中';
      setTimeout(() => {
        btn.classList.remove('is-disabled');
        btn.innerHTML = old;
        toast(btn.dataset.test + ' 连接正常 · 延迟 ' + (120 + Math.floor(Math.random() * 260)) + 'ms');
      }, 1100);
    });
  });

  /* ---------------- 其他可点击演示元素 ---------------- */
  document.querySelectorAll('[data-demo]').forEach((el) => {
    el.addEventListener('click', (e) => {
      e.preventDefault();
      toast(el.dataset.demo);
    });
  });

  /* ---------------- 生成按钮（演示：走一遍加载态） ---------------- */
  document.querySelectorAll('[data-generate]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const loading = btn.dataset.generate ? document.getElementById(btn.dataset.generate) : null;
      if (!loading) {
        toast('已提交生成任务');
        return;
      }
      loading.style.display = '';
      loading.scrollIntoView({ behavior: 'smooth', block: 'center' });
      let p = 6;
      const bar = loading.querySelector('.bar i');
      const pct = loading.querySelector('.pct');
      const timer = setInterval(() => {
        p = Math.min(100, p + Math.random() * 16);
        if (bar) bar.style.width = p.toFixed(0) + '%';
        if (pct) pct.textContent = p.toFixed(0) + '%';
        if (p >= 100) {
          clearInterval(timer);
          toast('生成完成（原型演示）');
        }
      }, 420);
    });
  });
})();
