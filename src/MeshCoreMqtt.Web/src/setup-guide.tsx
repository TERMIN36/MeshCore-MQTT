import { useEffect, useState, type ReactNode } from "react";

export function SetupHint() {
  const [open, setOpen] = useState(false);
  return (
    <>
      <button type="button" className="secondary" onClick={() => setOpen(true)}>Как настроить Wi‑Fi и подключить репитер</button>
      {open && <SetupGuide onClose={() => setOpen(false)} />}
    </>
  );
}

function SetupGuide({ onClose }: { onClose: () => void }) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === "Escape") onClose();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div
        className="modal-card guide"
        role="dialog"
        aria-modal="true"
        aria-labelledby="setup-guide-title"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="guide-head">
          <div>
            <h2 id="setup-guide-title">Как настроить Wi‑Fi и подключить репитер</h2>
            <p className="muted">Репитер входит в вашу сеть 2.4 ГГц как обычный клиент. Свою точку доступа он не поднимает. Имя сети задаётся по USB, а строка MQTT вставляется на странице репитера, когда у него уже есть адрес.</p>
          </div>
          <button type="button" className="secondary" onClick={onClose} autoFocus>Закрыть</button>
        </div>
        <ol className="guide-steps">
          <li className="guide-step">
            <Figure label="Скопированная строка настройки">
              <rect x="16" y="28" width="168" height="72" rx="6" fill="#191d22" stroke="#2b3138" />
              <text x="28" y="50" fill="#9aa4ae" fontSize="8">Строка настройки</text>
              <rect x="28" y="58" width="144" height="18" rx="3" fill="#0d0f12" stroke="#3b434d" />
              <text x="34" y="70" fill="#edf0f3" fontSize="8" fontFamily="ui-monospace, monospace">eyJ2IjoxLCJob3N0Ijoi…</text>
            </Figure>
            <div>
              <h3>Скопируйте строку настройки</h3>
              <p>Нажмите «Добавить» и «Копировать настройку». Строку вставляют на странице репитера целиком, разбирать её не нужно. Пароль панель показывает один раз: если строка потеряна, удалите репитер и добавьте снова.</p>
            </div>
          </li>
          <li className="guide-step">
            <Figure label="Репитер подключён кабелем USB к компьютеру, консоль 115200">
              <rect x="22" y="36" width="46" height="58" rx="6" fill="#20252b" stroke="#3b434d" />
              <rect x="40" y="22" width="6" height="16" fill="#9aa4ae" />
              <circle cx="43" cy="18" r="3" fill="#60a5fa" />
              <path d="M68 64 H92" stroke="#60a5fa" strokeWidth="2" />
              <rect x="92" y="40" width="86" height="48" rx="4" fill="#0d0f12" stroke="#3b434d" />
              <text x="100" y="58" fill="#9aa4ae" fontSize="8">USB</text>
              <text x="100" y="74" fill="#edf0f3" fontSize="8">115200</text>
            </Figure>
            <div>
              <h3>Откройте консоль по USB</h3>
              <p>Подключите репитер кабелем к компьютеру. В последовательной консоли скорость 115200. Подойдёт консоль веб-прошивальщика или любой терминал. Команда завершается Enter, ответ приходит строкой <span className="mono">-&gt;</span>.</p>
              <p>Короткий клик по кнопке репитера открывает экран Wi‑Fi: состояние, имя сети и адрес. Тройной клик на этом экране включает и выключает Wi‑Fi. Имя сети и пароль с кнопки не вводятся.</p>
            </div>
          </li>
          <li className="guide-step">
            <Figure label="Команды set wifi.ssid, set wifi.password и set wifi on">
              <Terminal lines={[
                "set wifi.ssid Дом",
                "  -> OK",
                "set wifi on",
                "get wifi",
                "  -> > on 192.168.1.20"
              ]} />
            </Figure>
            <div>
              <h3>Введите сеть 2.4 ГГц</h3>
              <p>Репитер видит только 2.4 ГГц. Имя сети — до 32 символов, пароль — до 63. Пробелы в имени и пароле допустимы, кавычки писать не нужно: в настройку попадёт всё после первого пробела.</p>
              <pre className="cli">{`set wifi.ssid ИмяСети
set wifi.password ПарольСети
set wifi on
get wifi`}</pre>
              <p>Ответ <span className="mono">&gt; on 192.168.x.x</span> значит, что адрес получен. Пока адреса нет, <span className="mono">get wifi</span> отвечает <span className="mono">&gt; on</span>. Для открытой сети пароль сбрасывается командой <span className="mono">set wifi.password</span> без значения.</p>
            </div>
          </li>
          <li className="guide-step">
            <Figure label="Форма «Подключение MQTT»: строка настройки и кнопка «Подключить»">
              <Browser address="192.168.1.20">
                <text x="28" y="50" fill="#edf0f3" fontSize="8">Подключение MQTT</text>
                <rect x="28" y="58" width="144" height="22" rx="3" fill="#0d0f12" stroke="#3b434d" />
                <text x="34" y="72" fill="#9aa4ae" fontSize="7">строка настройки</text>
                <rect x="28" y="86" width="64" height="12" rx="3" fill="#2563eb" />
                <text x="34" y="95" fill="#edf0f3" fontSize="7">Подключить</text>
              </Browser>
            </Figure>
            <div>
              <h3>Вставьте строку на странице репитера</h3>
              <p>Когда <span className="mono">get wifi</span> показал адрес, откройте в браузере <span className="mono">http://этот-адрес</span>. Порт 80. Логин <span className="mono">admin</span>, пароль — пароль администратора репитера. Его задают командой <span className="mono">password …</span> (до 15 символов). Пока пароль пустой, страница ждёт пустой пароль.</p>
              <p>В разделе «Подключение MQTT» вставьте скопированную строку целиком и нажмите «Подключить». Репитер сам возьмёт адрес, порт 8883, логин, пароль и сертификат и включит TLS. Имя топика вводить не нужно: публикация идёт от публичного ключа репитера, а сервер сам ограничивает её этим тунелем.</p>
            </div>
          </li>
          <li className="guide-step">
            <Figure label="В тунеле появились сообщения репитера">
              <rect x="16" y="28" width="168" height="72" rx="6" fill="#191d22" stroke="#2b3138" />
              <text x="28" y="48" fill="#edf0f3" fontSize="9">Сообщения</text>
              <rect x="28" y="58" width="144" height="14" rx="3" fill="#0d0f12" />
              <text x="34" y="68" fill="#34d399" fontSize="8">есть публикации</text>
              <rect x="28" y="76" width="144" height="14" rx="3" fill="#0d0f12" />
              <text x="34" y="86" fill="#9aa4ae" fontSize="8">get mqtt  →  up</text>
            </Figure>
            <div>
              <h3>Проверьте связь в этой панели</h3>
              <p>Откройте «Сообщения» тунеля. Публикации появляются, когда мост дошёл до сервера. Если список пуст, по USB проверьте <span className="mono">get wifi</span> и <span className="mono">get mqtt</span>: нужен адрес в сети 2.4 ГГц и ответ <span className="mono">&gt; up</span>. На странице репитера в разделе «Сеть» в это же время MQTT стоит как «подключён».</p>
            </div>
          </li>
        </ol>
      </div>
    </div>
  );
}

function Figure({ label, children }: { label: string; children: ReactNode }) {
  return (
    <figure className="guide-art">
      <svg viewBox="0 0 200 128" role="img" aria-label={label}>
        <rect width="200" height="128" fill="#14171b" />
        {children}
      </svg>
    </figure>
  );
}

function Terminal({ lines }: { lines: string[] }) {
  return (
    <>
      <rect x="16" y="16" width="168" height="96" rx="6" fill="#0d0f12" stroke="#2b3138" />
      <circle cx="28" cy="28" r="3" fill="#fb7185" />
      <circle cx="38" cy="28" r="3" fill="#fbbf24" />
      <circle cx="48" cy="28" r="3" fill="#34d399" />
      {lines.map((line, index) => (
        <text key={line} x="24" y={48 + index * 12} fill={line.startsWith("  ->") ? "#34d399" : "#edf0f3"} fontSize="8" fontFamily="ui-monospace, monospace">{line}</text>
      ))}
    </>
  );
}

function Browser({ address, children }: { address: string; children: ReactNode }) {
  return (
    <>
      <rect x="16" y="16" width="168" height="100" rx="6" fill="#191d22" stroke="#2b3138" />
      <rect x="24" y="24" width="152" height="14" rx="7" fill="#0d0f12" stroke="#2b3138" />
      <circle cx="34" cy="31" r="3" fill="#34d399" />
      <text x="42" y="34" fill="#9aa4ae" fontSize="8">{address}</text>
      {children}
    </>
  );
}
