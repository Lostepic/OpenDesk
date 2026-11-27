package org.opendesk.viewer;

import android.app.Activity;
import android.content.Context;
import android.graphics.Color;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.text.InputType;
import android.util.DisplayMetrics;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.webkit.CookieManager;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.regex.Pattern;

public final class ViewerActivity extends Activity {
    private static final int BACKGROUND = Color.rgb(18, 34, 38);
    private static final int TEXT = Color.rgb(234, 247, 243);
    private static final int MUTED = Color.rgb(169, 189, 188);
    private static final int MINT = Color.rgb(138, 239, 207);
    private static final int DISCOVERY_PORT = 47825;
    private static final Pattern ADDRESS = Pattern.compile("(?:[0-9]{1,3}\\.){3}[0-9]{1,3}");
    private static final Pattern INVITE = Pattern.compile("http://([0-9.]+):4782[67]/#[a-f0-9]{32}");
    private static final Pattern HOST_ID = Pattern.compile("[a-f0-9]{32}");

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private final Runnable retry = this::tryReconnect;
    private WebView viewer;
    private String lastAddress;
    private String hostId;
    private String activeInvite;
    private String deviceKey;
    private int screen;
    private int retryDelay = 1800;
    private boolean reconnectEnabled;
    private boolean waiting;
    private boolean foreground;
    private boolean retryRunning;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(BACKGROUND);
        getWindow().setNavigationBarColor(BACKGROUND);
        lastAddress = getPreferences(MODE_PRIVATE).getString("address", "");
        hostId = getPreferences(MODE_PRIVATE).getString("host", "");
        reconnectEnabled = getPreferences(MODE_PRIVATE).getBoolean("reconnect", false) &&
            HOST_ID.matcher(hostId).matches();
        deviceKey = getPreferences(MODE_PRIVATE).getString("device", "");
        if (deviceKey.isEmpty()) {
            byte[] random = new byte[16];
            new java.security.SecureRandom().nextBytes(random);
            StringBuilder hex = new StringBuilder(32);
            for (byte value : random)
                hex.append(Character.forDigit((value & 255) >> 4, 16))
                    .append(Character.forDigit(value & 15, 16));
            deviceKey = hex.toString();
            getPreferences(MODE_PRIVATE).edit().putString("device", deviceKey).apply();
        }
        activeInvite = state == null ? null : state.getString("activeInvite");
        if (activeInvite != null && validInvite(activeInvite, lastAddress)) {
            openViewer(activeInvite);
        } else if (reconnectEnabled) {
            showWaiting();
        } else {
            showSetup(null);
        }
    }

    private void showSetup(String message) {
        int version = ++screen;
        waiting = false;
        main.removeCallbacks(retry);
        closeViewer();
        activeInvite = null;
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().getDecorView().setSystemUiVisibility(0);

        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(BACKGROUND);
        FrameLayout center = new FrameLayout(this);
        center.setPadding(dp(24), dp(24), dp(24), dp(24));
        center.setFocusableInTouchMode(true);
        scroll.addView(center, new ScrollView.LayoutParams(-1, -1));

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        int width = Math.max(dp(160), getResources().getDisplayMetrics().widthPixels - dp(48));
        center.addView(panel, new FrameLayout.LayoutParams(Math.min(width, dp(530)), -2, Gravity.CENTER));

        TextView title = text("OpenDesk", TEXT, 34);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        panel.addView(title);
        if (message != null) addLine(panel, message, TEXT, 8);
        TextView status = addLine(panel, "Looking for PCs on your network…", MUTED, 8);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        LinearLayout computers = new LinearLayout(this);
        computers.setOrientation(LinearLayout.VERTICAL);
        panel.addView(computers);
        Button refresh = new Button(this);
        refresh.setText("Refresh");
        panel.addView(refresh, new LinearLayout.LayoutParams(-2, -2));
        TextView label = addLine(panel, "Connect by IP address", TEXT, 16);

        EditText address = new EditText(this);
        address.setId(View.generateViewId());
        label.setLabelFor(address.getId());
        address.setSingleLine(true);
        address.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        address.setImeOptions(EditorInfo.IME_ACTION_GO | EditorInfo.IME_FLAG_NO_EXTRACT_UI);
        address.setTextColor(TEXT);
        address.setHintTextColor(MUTED);
        address.setHint("192.168.1.10");
        address.setText(lastAddress);
        panel.addView(address, new LinearLayout.LayoutParams(-1, -2));

        Button connect = new Button(this);
        connect.setText("Connect");
        connect.setTextColor(Color.rgb(13, 38, 33));
        connect.setBackgroundTintList(android.content.res.ColorStateList.valueOf(MINT));
        LinearLayout.LayoutParams buttonLayout = new LinearLayout.LayoutParams(-2, -2);
        buttonLayout.topMargin = dp(11);
        panel.addView(connect, buttonLayout);

        java.util.function.Consumer<String> select = ip -> {
            if (!validAddress(ip)) {
                status.setText("Enter the Windows PC's IPv4 address.");
                address.requestFocus();
                return;
            }
            ((InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE))
                .hideSoftInputFromWindow(address.getWindowToken(), 0);
            setControls(computers, refresh, address, connect, false);
            status.setText("Connecting to " + ip + "… Approve on the PC if prompted.");
            worker.execute(() -> {
                try {
                    ConnectionReply reply = requestDisplay(ip, null);
                    runOnUiThread(() -> {
                        if (version != screen || isDestroyed()) return;
                        if (waiting) return;
                        remember(ip, reply);
                        openViewer(reply.invite);
                    });
                } catch (java.io.IOException error) {
                    runOnUiThread(() -> {
                        if (version != screen || isDestroyed()) return;
                        setControls(computers, refresh, address, connect, true);
                        status.setText(error instanceof HostReply ? error.getMessage()
                            : "Could not connect. Check OpenDesk and the local network.");
                    });
                }
            });
        };
        Runnable submit = () -> select.accept(address.getText().toString().trim());
        connect.setOnClickListener(view -> submit.run());
        address.setOnEditorActionListener((view, action, event) -> {
            if (action != EditorInfo.IME_ACTION_GO) return false;
            submit.run();
            return true;
        });
        Runnable look = () -> {
            refresh.setEnabled(false);
            connect.setEnabled(false);
            computers.removeAllViews();
            status.setText("Looking for PCs on your network…");
            worker.execute(() -> {
                Map<String, String> hosts = discover();
                runOnUiThread(() -> {
                    if (version != screen || isDestroyed()) return;
                    refresh.setEnabled(true);
                    connect.setEnabled(true);
                    status.setText(hosts.isEmpty() ? "No OpenDesk PCs found on this network." : "Choose your PC");
                    for (Map.Entry<String, String> host : hosts.entrySet()) {
                        Button item = new Button(this);
                        item.setText(host.getValue() + "  ·  " + host.getKey());
                        item.setOnClickListener(view -> select.accept(host.getKey()));
                        computers.addView(item, new LinearLayout.LayoutParams(-1, -2));
                    }
                });
            });
        };
        refresh.setOnClickListener(view -> look.run());
        setContentView(scroll);
        center.requestFocus();
        look.run();
    }

    private void remember(String ip, ConnectionReply reply) {
        lastAddress = ip;
        hostId = reply.host;
        reconnectEnabled = HOST_ID.matcher(hostId).matches();
        getPreferences(MODE_PRIVATE).edit().putString("address", ip).putString("host", hostId)
            .putBoolean("reconnect", reconnectEnabled).apply();
    }

    private void pauseReconnect() {
        reconnectEnabled = false;
        getPreferences(MODE_PRIVATE).edit().putBoolean("reconnect", false).apply();
    }

    private void showWaiting() {
        ++screen;
        waiting = true;
        retryDelay = 1800;
        closeViewer();
        activeInvite = null;
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().getDecorView().setSystemUiVisibility(0);
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setGravity(Gravity.CENTER);
        panel.setPadding(dp(24), dp(16), dp(24), dp(16));
        panel.setBackgroundColor(BACKGROUND);
        addLine(panel, "Waiting for OpenDesk on your PC", TEXT, 0);
        Button cancel = new Button(this);
        cancel.setText("Back to PCs");
        cancel.setOnClickListener(view -> {
            pauseReconnect();
            showSetup(null);
        });
        panel.addView(cancel, new LinearLayout.LayoutParams(-2, -2));
        setContentView(panel);
        scheduleReconnect(0);
    }

    private void scheduleReconnect(int delay) {
        main.removeCallbacks(retry);
        if (foreground && waiting && !retryRunning)
            main.postDelayed(retry, delay);
    }

    private void tryReconnect() {
        if (!waiting || !foreground || retryRunning) return;
        retryRunning = true;
        int version = screen;
        String rememberedAddress = lastAddress;
        String rememberedHost = hostId;
        worker.execute(() -> {
            String address = findRememberedHost(rememberedAddress, rememberedHost);
            ConnectionReply reply = null;
            boolean paused = false;
            if (address != null) {
                try { reply = requestDisplay(address, rememberedHost); }
                catch (HostReply error) { paused = error.code == 423; }
                catch (java.io.IOException ignored) { }
            }
            ConnectionReply connected = reply;
            boolean stop = paused;
            runOnUiThread(() -> {
                retryRunning = false;
                if (isDestroyed()) return;
                if (waiting && version == screen && foreground) {
                    if (stop) {
                        pauseReconnect();
                        showSetup("Select your PC to reconnect.");
                        return;
                    }
                    if (connected != null) {
                        remember(address, connected);
                        openViewer(connected.invite);
                        return;
                    }
                    retryDelay = Math.min(8000, retryDelay + 800);
                }
                scheduleReconnect(retryDelay);
            });
        });
    }

    private static String findRememberedHost(String address, String expected) {
        if (validAddress(address) && expected.equals(readIdentity(address)))
            return address;
        for (String candidate : discover().keySet()) {
            if (!candidate.equals(address) && expected.equals(readIdentity(candidate)))
                return candidate;
        }
        return null;
    }

    private static String readIdentity(String address) {
        HttpURLConnection request = null;
        try {
            request = (HttpURLConnection) new URL("http://" + address + ":" + DISCOVERY_PORT + "/identity")
                .openConnection(java.net.Proxy.NO_PROXY);
            request.setInstanceFollowRedirects(false);
            request.setConnectTimeout(1200);
            request.setReadTimeout(1200);
            if (request.getResponseCode() != 200) return "";
            try (InputStream body = request.getInputStream()) {
                byte[] bytes = new byte[33];
                int length = 0;
                while (length < bytes.length) {
                    int read = body.read(bytes, length, bytes.length - length);
                    if (read < 0) break;
                    length += read;
                }
                return new String(bytes, 0, length, StandardCharsets.UTF_8);
            }
        } catch (java.io.IOException ignored) {
            return "";
        } finally {
            if (request != null) request.disconnect();
        }
    }

    private void openViewer(String invite) {
        ++screen;
        waiting = false;
        main.removeCallbacks(retry);
        activeInvite = invite;
        WebView browser = new WebView(this);
        viewer = browser;
        browser.setBackgroundColor(BACKGROUND);
        WebSettings settings = browser.getSettings();
        settings.setUserAgentString(settings.getUserAgentString() + " OpenDeskAndroid/0.3");
        settings.setJavaScriptEnabled(true);
        settings.setSupportZoom(false);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setCacheMode(WebSettings.LOAD_NO_CACHE);
        CookieManager.getInstance().setAcceptCookie(false);

        FrameLayout surface = new FrameLayout(this);
        surface.addView(browser, new FrameLayout.LayoutParams(-1, -1));
        Button back = new Button(this);
        back.setText("Back to PCs");
        back.setOnClickListener(view -> disconnectViewer("Disconnected from " + lastAddress + ".", true));
        FrameLayout.LayoutParams backPosition = new FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM | Gravity.START);
        backPosition.setMargins(dp(10), dp(10), dp(10), dp(10));
        surface.addView(back, backPosition);

        browser.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, String url) {
                return handleViewerNavigation(view, invite, url);
            }

            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                if (!request.isForMainFrame()) return true;
                return handleViewerNavigation(view, invite, request.getUrl().toString());
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) reportConnectionError(browser);
            }

            @Override
            public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse response) {
                if (request.isForMainFrame()) reportConnectionError(browser);
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                if (viewer == browser && invite.equals(url)) {
                    browser.evaluateJavascript("Boolean(document.querySelector('#disconnect:not([hidden])'))", result -> {
                        if (viewer == browser && "true".equals(result))
                            back.setVisibility(View.GONE);
                    });
                }
            }
        });

        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setContentView(surface);
        enterImmersiveMode();
        browser.loadUrl(invite);
    }

    private boolean handleViewerNavigation(WebView browser, String invite, String url) {
        if (invite.equals(url)) return false;
        if (viewer == browser && invite.equals(browser.getUrl())) {
            boolean requested = url.equals("opendesk://disconnect");
            if (requested || url.equals("opendesk://connection-ended")) {
                browser.post(() -> {
                    if (viewer == browser)
                        disconnectViewer("Disconnected from " + lastAddress + ".", requested);
                });
            }
        }
        return true;
    }

    private void reportConnectionError(WebView browser) {
        browser.post(() -> {
            if (viewer == browser) {
                disconnectViewer("Could not reach OpenDesk. Check that the PC is sharing on the same network.", false);
            }
        });
    }

    private void disconnectViewer(String message, boolean notifyHost) {
        String invite = activeInvite;
        if (notifyHost) {
            pauseReconnect();
            if (invite != null) worker.execute(() -> releaseDisplay(Uri.parse(invite)));
        }
        if (reconnectEnabled) showWaiting();
        else showSetup(message);
    }

    @Override
    protected void onStart() {
        super.onStart();
        foreground = true;
        scheduleReconnect(0);
    }

    @Override
    protected void onStop() {
        foreground = false;
        main.removeCallbacks(retry);
        super.onStop();
    }

    private void enterImmersiveMode() {
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            | View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_FULLSCREEN);
    }

    @Override
    public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (focused && viewer != null) enterImmersiveMode();
    }

    @Override
    @SuppressWarnings("deprecation")
    public void onBackPressed() {
        if (viewer != null) disconnectViewer("Disconnected from " + lastAddress + ".", true);
        else if (waiting) {
            pauseReconnect();
            showSetup(null);
        } else super.onBackPressed();
    }

    @Override
    protected void onSaveInstanceState(Bundle state) {
        super.onSaveInstanceState(state);
        state.putString("activeInvite", activeInvite);
    }

    @Override
    protected void onDestroy() {
        main.removeCallbacks(retry);
        worker.shutdownNow();
        closeViewer();
        super.onDestroy();
    }

    private void closeViewer() {
        if (viewer == null) return;
        WebView browser = viewer;
        viewer = null;
        if (browser.getParent() instanceof android.view.ViewGroup) {
            ((android.view.ViewGroup) browser.getParent()).removeView(browser);
        }
        browser.stopLoading();
        browser.destroy();
    }

    private static boolean validAddress(String value) {
        if (!ADDRESS.matcher(value).matches()) return false;
        for (String octet : value.split("\\.")) {
            if (Integer.parseInt(octet) > 255 || (octet.length() > 1 && octet.startsWith("0"))) return false;
        }
        return true;
    }

    private static boolean validInvite(String value, String ip) {
        return INVITE.matcher(value).matches() && ip.equals(Uri.parse(value).getHost());
    }

    private static void setControls(LinearLayout computers, Button refresh, EditText address,
                                    Button connect, boolean enabled) {
        for (int index = 0; index < computers.getChildCount(); index++)
            computers.getChildAt(index).setEnabled(enabled);
        refresh.setEnabled(enabled);
        address.setEnabled(enabled);
        connect.setEnabled(enabled);
    }

    private static Map<String, String> discover() {
        Map<String, String> hosts = new LinkedHashMap<>();
        byte[] query = "OPENDESK/1 DISCOVER".getBytes(StandardCharsets.UTF_8);
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.setSoTimeout(300);
            try {
                socket.send(new DatagramPacket(query, query.length, InetAddress.getByName("255.255.255.255"),
                    DISCOVERY_PORT));
            } catch (java.io.IOException ignored) {
                // Still try each network's directed broadcast.
            }
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                for (InterfaceAddress iface : interfaces.nextElement().getInterfaceAddresses()) {
                    InetAddress broadcast = iface.getBroadcast();
                    if (broadcast != null) {
                        try {
                            socket.send(new DatagramPacket(query, query.length, broadcast, DISCOVERY_PORT));
                        } catch (java.io.IOException ignored) {
                            // A disconnected interface may have an address but no route.
                        }
                    }
                }
            }
            long end = SystemClock.elapsedRealtime() + 1300;
            while (SystemClock.elapsedRealtime() < end) {
                DatagramPacket packet = new DatagramPacket(new byte[128], 128);
                try {
                    socket.receive(packet);
                } catch (SocketTimeoutException idle) {
                    continue;
                }
                if (packet.getPort() != DISCOVERY_PORT) continue;
                String[] reply = new String(packet.getData(), 0, packet.getLength(),
                    StandardCharsets.UTF_8).split(" ", 3);
                if (reply.length == 3 && reply[0].equals("OPENDESK/1") && validAddress(reply[1])
                    && reply[1].equals(packet.getAddress().getHostAddress())
                    && reply[2].matches("[A-Za-z0-9_.-]{1,63}")) {
                    hosts.put(reply[1], reply[2]);
                }
            }
        } catch (java.io.IOException ignored) {
            // The manual address field remains available when broadcast is unavailable.
        }
        return hosts;
    }

    private ConnectionReply requestDisplay(String ip, String expectedHost) throws java.io.IOException {
        HttpURLConnection request = openBrokerRequest(ip, "POST", expectedHost == null ? 60000 : 7000);
        try {
            if (expectedHost != null) {
                request.setRequestProperty("X-OpenDesk-Auto", "1");
                request.setRequestProperty("X-OpenDesk-Host", expectedHost);
            }
            String name = Build.MODEL.replaceAll("[^A-Za-z0-9 ._()-]", " ").trim();
            request.setRequestProperty("X-OpenDesk-Name", name.substring(0, Math.min(40, name.length())));
            DisplayMetrics panel = new DisplayMetrics();
            getWindowManager().getDefaultDisplay().getRealMetrics(panel);
            request.setRequestProperty("X-OpenDesk-Width", Integer.toString(panel.widthPixels));
            request.setRequestProperty("X-OpenDesk-Height", Integer.toString(panel.heightPixels));
            int code = request.getResponseCode();
            try (InputStream response = code == 200 ? request.getInputStream() : request.getErrorStream()) {
                ByteArrayOutputStream result = new ByteArrayOutputStream();
                if (response != null) {
                    byte[] chunk = new byte[256];
                    int count;
                    while (result.size() <= 256 && (count = response.read(chunk)) != -1)
                        result.write(chunk, 0, count);
                }
                String text = new String(result.toByteArray(), StandardCharsets.UTF_8).trim();
                if (code != 200 && text.length() > 0 && text.length() <= 140)
                    throw new HostReply(text, code);
                String advertisedHost = request.getHeaderField("X-OpenDesk-Host");
                if (code != 200 || !validInvite(text, ip) ||
                    (expectedHost != null && !expectedHost.equals(advertisedHost)))
                    throw new java.io.IOException("Invalid OpenDesk response");
                return new ConnectionReply(text, advertisedHost == null ? "" : advertisedHost);
            }
        } finally {
            request.disconnect();
        }
    }

    private void releaseDisplay(Uri invite) {
        HttpURLConnection request = null;
        try {
            request = openBrokerRequest(invite.getHost(), "DELETE", 3000);
            request.setRequestProperty("X-OpenDesk-Session", invite.getFragment());
            request.getResponseCode();
        } catch (java.io.IOException ignored) {
            // Closing the stream still lets the host reclaim a disconnected viewer.
        } finally {
            if (request != null) request.disconnect();
        }
    }

    private HttpURLConnection openBrokerRequest(String ip, String method, int readTimeout)
        throws java.io.IOException {
        HttpURLConnection request = (HttpURLConnection) new URL("http://" + ip + ":" +
            DISCOVERY_PORT + "/connect").openConnection(java.net.Proxy.NO_PROXY);
        request.setConnectTimeout(Math.min(4500, readTimeout));
        request.setReadTimeout(readTimeout);
        request.setInstanceFollowRedirects(false);
        request.setRequestMethod(method);
        request.setRequestProperty("X-OpenDesk-Viewer", "1");
        request.setRequestProperty("X-OpenDesk-Device", deviceKey);
        request.setRequestProperty("Content-Length", "0");
        return request;
    }

    private static final class HostReply extends java.io.IOException {
        final int code;
        HostReply(String message, int code) {
            super(message);
            this.code = code;
        }
    }

    private static final class ConnectionReply {
        final String invite;
        final String host;
        ConnectionReply(String invite, String host) {
            this.invite = invite;
            this.host = host;
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private TextView text(String value, int color, int size) {
        TextView result = new TextView(this);
        result.setText(value);
        result.setTextColor(color);
        result.setTextSize(size);
        return result;
    }

    private TextView addLine(LinearLayout panel, String value, int color, int margin) {
        TextView line = text(value, color, 16);
        LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(-1, -2);
        layout.topMargin = dp(margin);
        panel.addView(line, layout);
        return line;
    }
}
