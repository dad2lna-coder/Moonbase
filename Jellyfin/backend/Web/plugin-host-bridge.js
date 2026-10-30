// Plugin client bridge. Native WebViews keep their injected MoFinHost; a sandboxed
// web iframe (opaque origin) gets the same API and talks to the parent by postMessage.
(function () {
  'use strict';

  if (window.MoFinHost) {
    return;
  }

  var nonce = '';
  try {
    var hash = location.hash || '';
    if (hash.charAt(0) === '#') {
      hash = hash.slice(1);
    }
    var params = new URLSearchParams(hash);
    if (params.has('mofinNonce')) {
      nonce = params.get('mofinNonce') || '';
      params.delete('mofinNonce');
      var rest = params.toString();
      history.replaceState(null, '', location.pathname + location.search + (rest ? '#' + rest : ''));
    }
  } catch (e) { /* The nonce already read still answers the parent. */ }

  window.MoFinHost = {
    _callbackMap: {},
    _callbackIdCounter: 0,
    _eventListeners: [],

    call: function (method, params) {
      return new Promise(function (resolve, reject) {
        var callbackId = 'cb_' + (++window.MoFinHost._callbackIdCounter) + '_' + Date.now();
        window.MoFinHost._callbackMap[callbackId] = { resolve: resolve, reject: reject };

        var payloadJson = JSON.stringify({
          method: method,
          params: params || {},
          callbackId: callbackId
        });

        try {
          if (window.flutter_inappwebview && typeof window.flutter_inappwebview.callHandler === 'function') {
            window.flutter_inappwebview.callHandler('MoFinPluginBridge', payloadJson);
          } else {
            window.parent.postMessage({ mofin: 1, nonce: nonce, payload: payloadJson }, '*');
          }
        } catch (err) {
          delete window.MoFinHost._callbackMap[callbackId];
          reject(err);
        }
      });
    },

    onEvent: function (callback) {
      if (typeof callback === 'function') {
        this._eventListeners.push(callback);
      }
    },

    _handleEvent: function (evt) {
      this._eventListeners.forEach(function (cb) {
        try { cb(evt); } catch (e) { console.error('Error in event listener:', e); }
      });
    },

    _handleResponse: function (jsonStr) {
      try {
        var response = typeof jsonStr === 'string' ? JSON.parse(jsonStr) : jsonStr;
        var callbackId = response.callbackId;
        if (callbackId && this._callbackMap[callbackId]) {
          var pending = this._callbackMap[callbackId];
          delete this._callbackMap[callbackId];
          if (response.ok) {
            pending.resolve(response.result);
          } else {
            pending.reject(new Error(response.error || 'Unknown bridge error'));
          }
        }
      } catch (e) {
        console.error('Error handling MoFinHost response:', e);
      }
    }
  };

  window.addEventListener('message', function (event) {
    var data = event.data;
    if (event.source !== window.parent || !data || data.mofin !== 1 || data.nonce !== nonce) {
      return;
    }

    if (data.type === 'response') {
      window.MoFinHost._handleResponse(data.data);
    } else if (data.type === 'event') {
      window.MoFinHost._handleEvent(data.data);
    }
  });
})();
