import {
  __commonJS,
  __toESM,
  require_react
} from "./chunk-66VBOVDU.js";

// node_modules/react/cjs/react-jsx-runtime.development.js
var require_react_jsx_runtime_development = __commonJS({
  "node_modules/react/cjs/react-jsx-runtime.development.js"(exports) {
    "use strict";
    if (true) {
      (function() {
        "use strict";
        var React = require_react();
        var REACT_ELEMENT_TYPE = Symbol.for("react.element");
        var REACT_PORTAL_TYPE = Symbol.for("react.portal");
        var REACT_FRAGMENT_TYPE = Symbol.for("react.fragment");
        var REACT_STRICT_MODE_TYPE = Symbol.for("react.strict_mode");
        var REACT_PROFILER_TYPE = Symbol.for("react.profiler");
        var REACT_PROVIDER_TYPE = Symbol.for("react.provider");
        var REACT_CONTEXT_TYPE = Symbol.for("react.context");
        var REACT_FORWARD_REF_TYPE = Symbol.for("react.forward_ref");
        var REACT_SUSPENSE_TYPE = Symbol.for("react.suspense");
        var REACT_SUSPENSE_LIST_TYPE = Symbol.for("react.suspense_list");
        var REACT_MEMO_TYPE = Symbol.for("react.memo");
        var REACT_LAZY_TYPE = Symbol.for("react.lazy");
        var REACT_OFFSCREEN_TYPE = Symbol.for("react.offscreen");
        var MAYBE_ITERATOR_SYMBOL = Symbol.iterator;
        var FAUX_ITERATOR_SYMBOL = "@@iterator";
        function getIteratorFn(maybeIterable) {
          if (maybeIterable === null || typeof maybeIterable !== "object") {
            return null;
          }
          var maybeIterator = MAYBE_ITERATOR_SYMBOL && maybeIterable[MAYBE_ITERATOR_SYMBOL] || maybeIterable[FAUX_ITERATOR_SYMBOL];
          if (typeof maybeIterator === "function") {
            return maybeIterator;
          }
          return null;
        }
        var ReactSharedInternals = React.__SECRET_INTERNALS_DO_NOT_USE_OR_YOU_WILL_BE_FIRED;
        function error(format) {
          {
            {
              for (var _len2 = arguments.length, args = new Array(_len2 > 1 ? _len2 - 1 : 0), _key2 = 1; _key2 < _len2; _key2++) {
                args[_key2 - 1] = arguments[_key2];
              }
              printWarning("error", format, args);
            }
          }
        }
        function printWarning(level, format, args) {
          {
            var ReactDebugCurrentFrame2 = ReactSharedInternals.ReactDebugCurrentFrame;
            var stack = ReactDebugCurrentFrame2.getStackAddendum();
            if (stack !== "") {
              format += "%s";
              args = args.concat([stack]);
            }
            var argsWithFormat = args.map(function(item) {
              return String(item);
            });
            argsWithFormat.unshift("Warning: " + format);
            Function.prototype.apply.call(console[level], console, argsWithFormat);
          }
        }
        var enableScopeAPI = false;
        var enableCacheElement = false;
        var enableTransitionTracing = false;
        var enableLegacyHidden = false;
        var enableDebugTracing = false;
        var REACT_MODULE_REFERENCE;
        {
          REACT_MODULE_REFERENCE = Symbol.for("react.module.reference");
        }
        function isValidElementType(type) {
          if (typeof type === "string" || typeof type === "function") {
            return true;
          }
          if (type === REACT_FRAGMENT_TYPE || type === REACT_PROFILER_TYPE || enableDebugTracing || type === REACT_STRICT_MODE_TYPE || type === REACT_SUSPENSE_TYPE || type === REACT_SUSPENSE_LIST_TYPE || enableLegacyHidden || type === REACT_OFFSCREEN_TYPE || enableScopeAPI || enableCacheElement || enableTransitionTracing) {
            return true;
          }
          if (typeof type === "object" && type !== null) {
            if (type.$$typeof === REACT_LAZY_TYPE || type.$$typeof === REACT_MEMO_TYPE || type.$$typeof === REACT_PROVIDER_TYPE || type.$$typeof === REACT_CONTEXT_TYPE || type.$$typeof === REACT_FORWARD_REF_TYPE || // This needs to include all possible module reference object
            // types supported by any Flight configuration anywhere since
            // we don't know which Flight build this will end up being used
            // with.
            type.$$typeof === REACT_MODULE_REFERENCE || type.getModuleId !== void 0) {
              return true;
            }
          }
          return false;
        }
        function getWrappedName(outerType, innerType, wrapperName) {
          var displayName = outerType.displayName;
          if (displayName) {
            return displayName;
          }
          var functionName = innerType.displayName || innerType.name || "";
          return functionName !== "" ? wrapperName + "(" + functionName + ")" : wrapperName;
        }
        function getContextName(type) {
          return type.displayName || "Context";
        }
        function getComponentNameFromType(type) {
          if (type == null) {
            return null;
          }
          {
            if (typeof type.tag === "number") {
              error("Received an unexpected object in getComponentNameFromType(). This is likely a bug in React. Please file an issue.");
            }
          }
          if (typeof type === "function") {
            return type.displayName || type.name || null;
          }
          if (typeof type === "string") {
            return type;
          }
          switch (type) {
            case REACT_FRAGMENT_TYPE:
              return "Fragment";
            case REACT_PORTAL_TYPE:
              return "Portal";
            case REACT_PROFILER_TYPE:
              return "Profiler";
            case REACT_STRICT_MODE_TYPE:
              return "StrictMode";
            case REACT_SUSPENSE_TYPE:
              return "Suspense";
            case REACT_SUSPENSE_LIST_TYPE:
              return "SuspenseList";
          }
          if (typeof type === "object") {
            switch (type.$$typeof) {
              case REACT_CONTEXT_TYPE:
                var context = type;
                return getContextName(context) + ".Consumer";
              case REACT_PROVIDER_TYPE:
                var provider = type;
                return getContextName(provider._context) + ".Provider";
              case REACT_FORWARD_REF_TYPE:
                return getWrappedName(type, type.render, "ForwardRef");
              case REACT_MEMO_TYPE:
                var outerName = type.displayName || null;
                if (outerName !== null) {
                  return outerName;
                }
                return getComponentNameFromType(type.type) || "Memo";
              case REACT_LAZY_TYPE: {
                var lazyComponent = type;
                var payload = lazyComponent._payload;
                var init = lazyComponent._init;
                try {
                  return getComponentNameFromType(init(payload));
                } catch (x) {
                  return null;
                }
              }
            }
          }
          return null;
        }
        var assign = Object.assign;
        var disabledDepth = 0;
        var prevLog;
        var prevInfo;
        var prevWarn;
        var prevError;
        var prevGroup;
        var prevGroupCollapsed;
        var prevGroupEnd;
        function disabledLog() {
        }
        disabledLog.__reactDisabledLog = true;
        function disableLogs() {
          {
            if (disabledDepth === 0) {
              prevLog = console.log;
              prevInfo = console.info;
              prevWarn = console.warn;
              prevError = console.error;
              prevGroup = console.group;
              prevGroupCollapsed = console.groupCollapsed;
              prevGroupEnd = console.groupEnd;
              var props = {
                configurable: true,
                enumerable: true,
                value: disabledLog,
                writable: true
              };
              Object.defineProperties(console, {
                info: props,
                log: props,
                warn: props,
                error: props,
                group: props,
                groupCollapsed: props,
                groupEnd: props
              });
            }
            disabledDepth++;
          }
        }
        function reenableLogs() {
          {
            disabledDepth--;
            if (disabledDepth === 0) {
              var props = {
                configurable: true,
                enumerable: true,
                writable: true
              };
              Object.defineProperties(console, {
                log: assign({}, props, {
                  value: prevLog
                }),
                info: assign({}, props, {
                  value: prevInfo
                }),
                warn: assign({}, props, {
                  value: prevWarn
                }),
                error: assign({}, props, {
                  value: prevError
                }),
                group: assign({}, props, {
                  value: prevGroup
                }),
                groupCollapsed: assign({}, props, {
                  value: prevGroupCollapsed
                }),
                groupEnd: assign({}, props, {
                  value: prevGroupEnd
                })
              });
            }
            if (disabledDepth < 0) {
              error("disabledDepth fell below zero. This is a bug in React. Please file an issue.");
            }
          }
        }
        var ReactCurrentDispatcher = ReactSharedInternals.ReactCurrentDispatcher;
        var prefix;
        function describeBuiltInComponentFrame(name, source, ownerFn) {
          {
            if (prefix === void 0) {
              try {
                throw Error();
              } catch (x) {
                var match = x.stack.trim().match(/\n( *(at )?)/);
                prefix = match && match[1] || "";
              }
            }
            return "\n" + prefix + name;
          }
        }
        var reentry = false;
        var componentFrameCache;
        {
          var PossiblyWeakMap = typeof WeakMap === "function" ? WeakMap : Map;
          componentFrameCache = new PossiblyWeakMap();
        }
        function describeNativeComponentFrame(fn, construct) {
          if (!fn || reentry) {
            return "";
          }
          {
            var frame = componentFrameCache.get(fn);
            if (frame !== void 0) {
              return frame;
            }
          }
          var control;
          reentry = true;
          var previousPrepareStackTrace = Error.prepareStackTrace;
          Error.prepareStackTrace = void 0;
          var previousDispatcher;
          {
            previousDispatcher = ReactCurrentDispatcher.current;
            ReactCurrentDispatcher.current = null;
            disableLogs();
          }
          try {
            if (construct) {
              var Fake = function() {
                throw Error();
              };
              Object.defineProperty(Fake.prototype, "props", {
                set: function() {
                  throw Error();
                }
              });
              if (typeof Reflect === "object" && Reflect.construct) {
                try {
                  Reflect.construct(Fake, []);
                } catch (x) {
                  control = x;
                }
                Reflect.construct(fn, [], Fake);
              } else {
                try {
                  Fake.call();
                } catch (x) {
                  control = x;
                }
                fn.call(Fake.prototype);
              }
            } else {
              try {
                throw Error();
              } catch (x) {
                control = x;
              }
              fn();
            }
          } catch (sample) {
            if (sample && control && typeof sample.stack === "string") {
              var sampleLines = sample.stack.split("\n");
              var controlLines = control.stack.split("\n");
              var s = sampleLines.length - 1;
              var c = controlLines.length - 1;
              while (s >= 1 && c >= 0 && sampleLines[s] !== controlLines[c]) {
                c--;
              }
              for (; s >= 1 && c >= 0; s--, c--) {
                if (sampleLines[s] !== controlLines[c]) {
                  if (s !== 1 || c !== 1) {
                    do {
                      s--;
                      c--;
                      if (c < 0 || sampleLines[s] !== controlLines[c]) {
                        var _frame = "\n" + sampleLines[s].replace(" at new ", " at ");
                        if (fn.displayName && _frame.includes("<anonymous>")) {
                          _frame = _frame.replace("<anonymous>", fn.displayName);
                        }
                        {
                          if (typeof fn === "function") {
                            componentFrameCache.set(fn, _frame);
                          }
                        }
                        return _frame;
                      }
                    } while (s >= 1 && c >= 0);
                  }
                  break;
                }
              }
            }
          } finally {
            reentry = false;
            {
              ReactCurrentDispatcher.current = previousDispatcher;
              reenableLogs();
            }
            Error.prepareStackTrace = previousPrepareStackTrace;
          }
          var name = fn ? fn.displayName || fn.name : "";
          var syntheticFrame = name ? describeBuiltInComponentFrame(name) : "";
          {
            if (typeof fn === "function") {
              componentFrameCache.set(fn, syntheticFrame);
            }
          }
          return syntheticFrame;
        }
        function describeFunctionComponentFrame(fn, source, ownerFn) {
          {
            return describeNativeComponentFrame(fn, false);
          }
        }
        function shouldConstruct(Component) {
          var prototype = Component.prototype;
          return !!(prototype && prototype.isReactComponent);
        }
        function describeUnknownElementTypeFrameInDEV(type, source, ownerFn) {
          if (type == null) {
            return "";
          }
          if (typeof type === "function") {
            {
              return describeNativeComponentFrame(type, shouldConstruct(type));
            }
          }
          if (typeof type === "string") {
            return describeBuiltInComponentFrame(type);
          }
          switch (type) {
            case REACT_SUSPENSE_TYPE:
              return describeBuiltInComponentFrame("Suspense");
            case REACT_SUSPENSE_LIST_TYPE:
              return describeBuiltInComponentFrame("SuspenseList");
          }
          if (typeof type === "object") {
            switch (type.$$typeof) {
              case REACT_FORWARD_REF_TYPE:
                return describeFunctionComponentFrame(type.render);
              case REACT_MEMO_TYPE:
                return describeUnknownElementTypeFrameInDEV(type.type, source, ownerFn);
              case REACT_LAZY_TYPE: {
                var lazyComponent = type;
                var payload = lazyComponent._payload;
                var init = lazyComponent._init;
                try {
                  return describeUnknownElementTypeFrameInDEV(init(payload), source, ownerFn);
                } catch (x) {
                }
              }
            }
          }
          return "";
        }
        var hasOwnProperty = Object.prototype.hasOwnProperty;
        var loggedTypeFailures = {};
        var ReactDebugCurrentFrame = ReactSharedInternals.ReactDebugCurrentFrame;
        function setCurrentlyValidatingElement(element) {
          {
            if (element) {
              var owner = element._owner;
              var stack = describeUnknownElementTypeFrameInDEV(element.type, element._source, owner ? owner.type : null);
              ReactDebugCurrentFrame.setExtraStackFrame(stack);
            } else {
              ReactDebugCurrentFrame.setExtraStackFrame(null);
            }
          }
        }
        function checkPropTypes(typeSpecs, values, location, componentName, element) {
          {
            var has = Function.call.bind(hasOwnProperty);
            for (var typeSpecName in typeSpecs) {
              if (has(typeSpecs, typeSpecName)) {
                var error$1 = void 0;
                try {
                  if (typeof typeSpecs[typeSpecName] !== "function") {
                    var err = Error((componentName || "React class") + ": " + location + " type `" + typeSpecName + "` is invalid; it must be a function, usually from the `prop-types` package, but received `" + typeof typeSpecs[typeSpecName] + "`.This often happens because of typos such as `PropTypes.function` instead of `PropTypes.func`.");
                    err.name = "Invariant Violation";
                    throw err;
                  }
                  error$1 = typeSpecs[typeSpecName](values, typeSpecName, componentName, location, null, "SECRET_DO_NOT_PASS_THIS_OR_YOU_WILL_BE_FIRED");
                } catch (ex) {
                  error$1 = ex;
                }
                if (error$1 && !(error$1 instanceof Error)) {
                  setCurrentlyValidatingElement(element);
                  error("%s: type specification of %s `%s` is invalid; the type checker function must return `null` or an `Error` but returned a %s. You may have forgotten to pass an argument to the type checker creator (arrayOf, instanceOf, objectOf, oneOf, oneOfType, and shape all require an argument).", componentName || "React class", location, typeSpecName, typeof error$1);
                  setCurrentlyValidatingElement(null);
                }
                if (error$1 instanceof Error && !(error$1.message in loggedTypeFailures)) {
                  loggedTypeFailures[error$1.message] = true;
                  setCurrentlyValidatingElement(element);
                  error("Failed %s type: %s", location, error$1.message);
                  setCurrentlyValidatingElement(null);
                }
              }
            }
          }
        }
        var isArrayImpl = Array.isArray;
        function isArray(a) {
          return isArrayImpl(a);
        }
        function typeName(value) {
          {
            var hasToStringTag = typeof Symbol === "function" && Symbol.toStringTag;
            var type = hasToStringTag && value[Symbol.toStringTag] || value.constructor.name || "Object";
            return type;
          }
        }
        function willCoercionThrow(value) {
          {
            try {
              testStringCoercion(value);
              return false;
            } catch (e) {
              return true;
            }
          }
        }
        function testStringCoercion(value) {
          return "" + value;
        }
        function checkKeyStringCoercion(value) {
          {
            if (willCoercionThrow(value)) {
              error("The provided key is an unsupported type %s. This value must be coerced to a string before before using it here.", typeName(value));
              return testStringCoercion(value);
            }
          }
        }
        var ReactCurrentOwner = ReactSharedInternals.ReactCurrentOwner;
        var RESERVED_PROPS = {
          key: true,
          ref: true,
          __self: true,
          __source: true
        };
        var specialPropKeyWarningShown;
        var specialPropRefWarningShown;
        var didWarnAboutStringRefs;
        {
          didWarnAboutStringRefs = {};
        }
        function hasValidRef(config) {
          {
            if (hasOwnProperty.call(config, "ref")) {
              var getter = Object.getOwnPropertyDescriptor(config, "ref").get;
              if (getter && getter.isReactWarning) {
                return false;
              }
            }
          }
          return config.ref !== void 0;
        }
        function hasValidKey(config) {
          {
            if (hasOwnProperty.call(config, "key")) {
              var getter = Object.getOwnPropertyDescriptor(config, "key").get;
              if (getter && getter.isReactWarning) {
                return false;
              }
            }
          }
          return config.key !== void 0;
        }
        function warnIfStringRefCannotBeAutoConverted(config, self) {
          {
            if (typeof config.ref === "string" && ReactCurrentOwner.current && self && ReactCurrentOwner.current.stateNode !== self) {
              var componentName = getComponentNameFromType(ReactCurrentOwner.current.type);
              if (!didWarnAboutStringRefs[componentName]) {
                error('Component "%s" contains the string ref "%s". Support for string refs will be removed in a future major release. This case cannot be automatically converted to an arrow function. We ask you to manually fix this case by using useRef() or createRef() instead. Learn more about using refs safely here: https://reactjs.org/link/strict-mode-string-ref', getComponentNameFromType(ReactCurrentOwner.current.type), config.ref);
                didWarnAboutStringRefs[componentName] = true;
              }
            }
          }
        }
        function defineKeyPropWarningGetter(props, displayName) {
          {
            var warnAboutAccessingKey = function() {
              if (!specialPropKeyWarningShown) {
                specialPropKeyWarningShown = true;
                error("%s: `key` is not a prop. Trying to access it will result in `undefined` being returned. If you need to access the same value within the child component, you should pass it as a different prop. (https://reactjs.org/link/special-props)", displayName);
              }
            };
            warnAboutAccessingKey.isReactWarning = true;
            Object.defineProperty(props, "key", {
              get: warnAboutAccessingKey,
              configurable: true
            });
          }
        }
        function defineRefPropWarningGetter(props, displayName) {
          {
            var warnAboutAccessingRef = function() {
              if (!specialPropRefWarningShown) {
                specialPropRefWarningShown = true;
                error("%s: `ref` is not a prop. Trying to access it will result in `undefined` being returned. If you need to access the same value within the child component, you should pass it as a different prop. (https://reactjs.org/link/special-props)", displayName);
              }
            };
            warnAboutAccessingRef.isReactWarning = true;
            Object.defineProperty(props, "ref", {
              get: warnAboutAccessingRef,
              configurable: true
            });
          }
        }
        var ReactElement = function(type, key, ref, self, source, owner, props) {
          var element = {
            // This tag allows us to uniquely identify this as a React Element
            $$typeof: REACT_ELEMENT_TYPE,
            // Built-in properties that belong on the element
            type,
            key,
            ref,
            props,
            // Record the component responsible for creating this element.
            _owner: owner
          };
          {
            element._store = {};
            Object.defineProperty(element._store, "validated", {
              configurable: false,
              enumerable: false,
              writable: true,
              value: false
            });
            Object.defineProperty(element, "_self", {
              configurable: false,
              enumerable: false,
              writable: false,
              value: self
            });
            Object.defineProperty(element, "_source", {
              configurable: false,
              enumerable: false,
              writable: false,
              value: source
            });
            if (Object.freeze) {
              Object.freeze(element.props);
              Object.freeze(element);
            }
          }
          return element;
        };
        function jsxDEV(type, config, maybeKey, source, self) {
          {
            var propName;
            var props = {};
            var key = null;
            var ref = null;
            if (maybeKey !== void 0) {
              {
                checkKeyStringCoercion(maybeKey);
              }
              key = "" + maybeKey;
            }
            if (hasValidKey(config)) {
              {
                checkKeyStringCoercion(config.key);
              }
              key = "" + config.key;
            }
            if (hasValidRef(config)) {
              ref = config.ref;
              warnIfStringRefCannotBeAutoConverted(config, self);
            }
            for (propName in config) {
              if (hasOwnProperty.call(config, propName) && !RESERVED_PROPS.hasOwnProperty(propName)) {
                props[propName] = config[propName];
              }
            }
            if (type && type.defaultProps) {
              var defaultProps = type.defaultProps;
              for (propName in defaultProps) {
                if (props[propName] === void 0) {
                  props[propName] = defaultProps[propName];
                }
              }
            }
            if (key || ref) {
              var displayName = typeof type === "function" ? type.displayName || type.name || "Unknown" : type;
              if (key) {
                defineKeyPropWarningGetter(props, displayName);
              }
              if (ref) {
                defineRefPropWarningGetter(props, displayName);
              }
            }
            return ReactElement(type, key, ref, self, source, ReactCurrentOwner.current, props);
          }
        }
        var ReactCurrentOwner$1 = ReactSharedInternals.ReactCurrentOwner;
        var ReactDebugCurrentFrame$1 = ReactSharedInternals.ReactDebugCurrentFrame;
        function setCurrentlyValidatingElement$1(element) {
          {
            if (element) {
              var owner = element._owner;
              var stack = describeUnknownElementTypeFrameInDEV(element.type, element._source, owner ? owner.type : null);
              ReactDebugCurrentFrame$1.setExtraStackFrame(stack);
            } else {
              ReactDebugCurrentFrame$1.setExtraStackFrame(null);
            }
          }
        }
        var propTypesMisspellWarningShown;
        {
          propTypesMisspellWarningShown = false;
        }
        function isValidElement(object) {
          {
            return typeof object === "object" && object !== null && object.$$typeof === REACT_ELEMENT_TYPE;
          }
        }
        function getDeclarationErrorAddendum() {
          {
            if (ReactCurrentOwner$1.current) {
              var name = getComponentNameFromType(ReactCurrentOwner$1.current.type);
              if (name) {
                return "\n\nCheck the render method of `" + name + "`.";
              }
            }
            return "";
          }
        }
        function getSourceInfoErrorAddendum(source) {
          {
            if (source !== void 0) {
              var fileName = source.fileName.replace(/^.*[\\\/]/, "");
              var lineNumber = source.lineNumber;
              return "\n\nCheck your code at " + fileName + ":" + lineNumber + ".";
            }
            return "";
          }
        }
        var ownerHasKeyUseWarning = {};
        function getCurrentComponentErrorInfo(parentType) {
          {
            var info = getDeclarationErrorAddendum();
            if (!info) {
              var parentName = typeof parentType === "string" ? parentType : parentType.displayName || parentType.name;
              if (parentName) {
                info = "\n\nCheck the top-level render call using <" + parentName + ">.";
              }
            }
            return info;
          }
        }
        function validateExplicitKey(element, parentType) {
          {
            if (!element._store || element._store.validated || element.key != null) {
              return;
            }
            element._store.validated = true;
            var currentComponentErrorInfo = getCurrentComponentErrorInfo(parentType);
            if (ownerHasKeyUseWarning[currentComponentErrorInfo]) {
              return;
            }
            ownerHasKeyUseWarning[currentComponentErrorInfo] = true;
            var childOwner = "";
            if (element && element._owner && element._owner !== ReactCurrentOwner$1.current) {
              childOwner = " It was passed a child from " + getComponentNameFromType(element._owner.type) + ".";
            }
            setCurrentlyValidatingElement$1(element);
            error('Each child in a list should have a unique "key" prop.%s%s See https://reactjs.org/link/warning-keys for more information.', currentComponentErrorInfo, childOwner);
            setCurrentlyValidatingElement$1(null);
          }
        }
        function validateChildKeys(node, parentType) {
          {
            if (typeof node !== "object") {
              return;
            }
            if (isArray(node)) {
              for (var i = 0; i < node.length; i++) {
                var child = node[i];
                if (isValidElement(child)) {
                  validateExplicitKey(child, parentType);
                }
              }
            } else if (isValidElement(node)) {
              if (node._store) {
                node._store.validated = true;
              }
            } else if (node) {
              var iteratorFn = getIteratorFn(node);
              if (typeof iteratorFn === "function") {
                if (iteratorFn !== node.entries) {
                  var iterator = iteratorFn.call(node);
                  var step;
                  while (!(step = iterator.next()).done) {
                    if (isValidElement(step.value)) {
                      validateExplicitKey(step.value, parentType);
                    }
                  }
                }
              }
            }
          }
        }
        function validatePropTypes(element) {
          {
            var type = element.type;
            if (type === null || type === void 0 || typeof type === "string") {
              return;
            }
            var propTypes;
            if (typeof type === "function") {
              propTypes = type.propTypes;
            } else if (typeof type === "object" && (type.$$typeof === REACT_FORWARD_REF_TYPE || // Note: Memo only checks outer props here.
            // Inner props are checked in the reconciler.
            type.$$typeof === REACT_MEMO_TYPE)) {
              propTypes = type.propTypes;
            } else {
              return;
            }
            if (propTypes) {
              var name = getComponentNameFromType(type);
              checkPropTypes(propTypes, element.props, "prop", name, element);
            } else if (type.PropTypes !== void 0 && !propTypesMisspellWarningShown) {
              propTypesMisspellWarningShown = true;
              var _name = getComponentNameFromType(type);
              error("Component %s declared `PropTypes` instead of `propTypes`. Did you misspell the property assignment?", _name || "Unknown");
            }
            if (typeof type.getDefaultProps === "function" && !type.getDefaultProps.isReactClassApproved) {
              error("getDefaultProps is only used on classic React.createClass definitions. Use a static property named `defaultProps` instead.");
            }
          }
        }
        function validateFragmentProps(fragment) {
          {
            var keys = Object.keys(fragment.props);
            for (var i = 0; i < keys.length; i++) {
              var key = keys[i];
              if (key !== "children" && key !== "key") {
                setCurrentlyValidatingElement$1(fragment);
                error("Invalid prop `%s` supplied to `React.Fragment`. React.Fragment can only have `key` and `children` props.", key);
                setCurrentlyValidatingElement$1(null);
                break;
              }
            }
            if (fragment.ref !== null) {
              setCurrentlyValidatingElement$1(fragment);
              error("Invalid attribute `ref` supplied to `React.Fragment`.");
              setCurrentlyValidatingElement$1(null);
            }
          }
        }
        var didWarnAboutKeySpread = {};
        function jsxWithValidation(type, props, key, isStaticChildren, source, self) {
          {
            var validType = isValidElementType(type);
            if (!validType) {
              var info = "";
              if (type === void 0 || typeof type === "object" && type !== null && Object.keys(type).length === 0) {
                info += " You likely forgot to export your component from the file it's defined in, or you might have mixed up default and named imports.";
              }
              var sourceInfo = getSourceInfoErrorAddendum(source);
              if (sourceInfo) {
                info += sourceInfo;
              } else {
                info += getDeclarationErrorAddendum();
              }
              var typeString;
              if (type === null) {
                typeString = "null";
              } else if (isArray(type)) {
                typeString = "array";
              } else if (type !== void 0 && type.$$typeof === REACT_ELEMENT_TYPE) {
                typeString = "<" + (getComponentNameFromType(type.type) || "Unknown") + " />";
                info = " Did you accidentally export a JSX literal instead of a component?";
              } else {
                typeString = typeof type;
              }
              error("React.jsx: type is invalid -- expected a string (for built-in components) or a class/function (for composite components) but got: %s.%s", typeString, info);
            }
            var element = jsxDEV(type, props, key, source, self);
            if (element == null) {
              return element;
            }
            if (validType) {
              var children = props.children;
              if (children !== void 0) {
                if (isStaticChildren) {
                  if (isArray(children)) {
                    for (var i = 0; i < children.length; i++) {
                      validateChildKeys(children[i], type);
                    }
                    if (Object.freeze) {
                      Object.freeze(children);
                    }
                  } else {
                    error("React.jsx: Static children should always be an array. You are likely explicitly calling React.jsxs or React.jsxDEV. Use the Babel transform instead.");
                  }
                } else {
                  validateChildKeys(children, type);
                }
              }
            }
            {
              if (hasOwnProperty.call(props, "key")) {
                var componentName = getComponentNameFromType(type);
                var keys = Object.keys(props).filter(function(k) {
                  return k !== "key";
                });
                var beforeExample = keys.length > 0 ? "{key: someKey, " + keys.join(": ..., ") + ": ...}" : "{key: someKey}";
                if (!didWarnAboutKeySpread[componentName + beforeExample]) {
                  var afterExample = keys.length > 0 ? "{" + keys.join(": ..., ") + ": ...}" : "{}";
                  error('A props object containing a "key" prop is being spread into JSX:\n  let props = %s;\n  <%s {...props} />\nReact keys must be passed directly to JSX without using spread:\n  let props = %s;\n  <%s key={someKey} {...props} />', beforeExample, componentName, afterExample, componentName);
                  didWarnAboutKeySpread[componentName + beforeExample] = true;
                }
              }
            }
            if (type === REACT_FRAGMENT_TYPE) {
              validateFragmentProps(element);
            } else {
              validatePropTypes(element);
            }
            return element;
          }
        }
        function jsxWithValidationStatic(type, props, key) {
          {
            return jsxWithValidation(type, props, key, true);
          }
        }
        function jsxWithValidationDynamic(type, props, key) {
          {
            return jsxWithValidation(type, props, key, false);
          }
        }
        var jsx = jsxWithValidationDynamic;
        var jsxs = jsxWithValidationStatic;
        exports.Fragment = REACT_FRAGMENT_TYPE;
        exports.jsx = jsx;
        exports.jsxs = jsxs;
      })();
    }
  }
});

// node_modules/react/jsx-runtime.js
var require_jsx_runtime = __commonJS({
  "node_modules/react/jsx-runtime.js"(exports, module) {
    "use strict";
    if (false) {
      module.exports = null;
    } else {
      module.exports = require_react_jsx_runtime_development();
    }
  }
});

// node_modules/react-window/dist/react-window.js
var import_jsx_runtime = __toESM(require_jsx_runtime());
var import_react = __toESM(require_react());
function xe(e) {
  let t = e;
  for (; t; ) {
    if (t.dir)
      return t.dir === "rtl";
    t = t.parentElement;
  }
  return false;
}
function ve(e, t) {
  const [s, r] = (0, import_react.useState)(t === "rtl");
  return (0, import_react.useLayoutEffect)(() => {
    e && (t || r(xe(e)));
  }, [t, e]), s;
}
var q = typeof window < "u" ? import_react.useLayoutEffect : import_react.useEffect;
function oe(e) {
  if (e !== void 0)
    switch (typeof e) {
      case "number":
        return e;
      case "string": {
        if (e.endsWith("px"))
          return parseFloat(e);
        break;
      }
    }
}
function Ie({
  box: e,
  defaultHeight: t,
  defaultWidth: s,
  disabled: r,
  element: n,
  mode: o,
  style: i
}) {
  const { styleHeight: f, styleWidth: l } = (0, import_react.useMemo)(
    () => ({
      styleHeight: oe(i == null ? void 0 : i.height),
      styleWidth: oe(i == null ? void 0 : i.width)
    }),
    [i == null ? void 0 : i.height, i == null ? void 0 : i.width]
  ), [c, d] = (0, import_react.useState)({
    height: t,
    width: s
  }), a = r || o === "only-height" && f !== void 0 || o === "only-width" && l !== void 0 || f !== void 0 && l !== void 0;
  return q(() => {
    if (n === null || a)
      return;
    const u = new ResizeObserver((I) => {
      for (const m of I) {
        const { contentRect: b, target: g } = m;
        n === g && d((w) => w.height === b.height && w.width === b.width ? w : {
          height: b.height,
          width: b.width
        });
      }
    });
    return u.observe(n, { box: e }), () => {
      u == null ? void 0 : u.unobserve(n);
    };
  }, [e, a, n, f, l]), (0, import_react.useMemo)(
    () => ({
      height: f ?? c.height,
      width: l ?? c.width
    }),
    [c, f, l]
  );
}
function ae(e) {
  const t = (0, import_react.useRef)(() => {
    throw new Error("Cannot call during render.");
  });
  return q(() => {
    t.current = e;
  }, [e]), (0, import_react.useCallback)((s) => {
    var _a;
    return (_a = t.current) == null ? void 0 : _a.call(t, s);
  }, [t]);
}
var F = null;
function be(e = false) {
  if (F === null || e) {
    const t = document.createElement("div"), s = t.style;
    s.width = "50px", s.height = "50px", s.overflow = "scroll", s.direction = "rtl";
    const r = document.createElement("div"), n = r.style;
    return n.width = "100px", n.height = "100px", t.appendChild(r), document.body.appendChild(t), t.scrollLeft > 0 ? F = "positive-descending" : (t.scrollLeft = 1, t.scrollLeft === 0 ? F = "negative" : F = "positive-ascending"), document.body.removeChild(t), F;
  }
  return F;
}
function Z({
  containerElement: e,
  direction: t,
  isRtl: s,
  scrollOffset: r
}) {
  if (t === "horizontal" && s)
    switch (be()) {
      case "negative":
        return -r;
      case "positive-descending": {
        if (e) {
          const { clientWidth: n, scrollLeft: o, scrollWidth: i } = e;
          return i - n - o;
        }
        break;
      }
    }
  return r;
}
function $(e, t = "Assertion error") {
  if (!e)
    throw console.error(t), Error(t);
}
function Y(e, t) {
  if (e === t)
    return true;
  if (!!e != !!t || ($(e !== void 0), $(t !== void 0), Object.keys(e).length !== Object.keys(t).length))
    return false;
  for (const s in e)
    if (!Object.is(t[s], e[s]))
      return false;
  return true;
}
function fe({
  cachedBounds: e,
  itemCount: t,
  itemSize: s
}) {
  if (t === 0)
    return 0;
  if (typeof s == "number")
    return t * s;
  {
    const r = e.get(
      e.size === 0 ? 0 : e.size - 1
    );
    $(r !== void 0, "Unexpected bounds cache miss");
    const n = (r.scrollOffset + r.size) / e.size;
    return t * n;
  }
}
function we({
  align: e,
  cachedBounds: t,
  index: s,
  itemCount: r,
  itemSize: n,
  containerScrollOffset: o,
  containerSize: i
}) {
  if (s < 0 || s >= r)
    throw RangeError(`Invalid index specified: ${s}`, {
      cause: `Index ${s} is not within the range of 0 - ${r - 1}`
    });
  const f = fe({
    cachedBounds: t,
    itemCount: r,
    itemSize: n
  }), l = t.get(s), c = Math.max(
    0,
    Math.min(f - i, l.scrollOffset)
  ), d = Math.max(
    0,
    l.scrollOffset - i + l.size
  );
  switch (e === "smart" && (o >= d && o <= c ? e = "auto" : e = "center"), e) {
    case "start":
      return c;
    case "end":
      return d;
    case "center":
      return l.scrollOffset <= i / 2 ? 0 : l.scrollOffset + l.size / 2 >= f - i / 2 ? f - i : l.scrollOffset + l.size / 2 - i / 2;
    case "auto":
    default:
      return o >= d && o <= c ? o : o < d ? d : c;
  }
}
function ie({
  cachedBounds: e,
  containerScrollOffset: t,
  containerSize: s,
  itemCount: r,
  overscanCount: n
}) {
  const o = r - 1;
  let i = 0, f = -1, l = 0, c = -1, d = 0;
  for (; d < o; ) {
    const a = e.get(d);
    if (a.scrollOffset + a.size > t)
      break;
    d++;
  }
  for (i = d, l = Math.max(0, i - n); d < o; ) {
    const a = e.get(d);
    if (a.scrollOffset + a.size >= t + s)
      break;
    d++;
  }
  return f = Math.min(o, d), c = Math.min(r - 1, f + n), i < 0 && (i = 0, f = -1, l = 0, c = -1), {
    startIndexVisible: i,
    stopIndexVisible: f,
    startIndexOverscan: l,
    stopIndexOverscan: c
  };
}
function me({
  itemCount: e,
  itemProps: t,
  itemSize: s
}) {
  const r = /* @__PURE__ */ new Map();
  return {
    get(n) {
      for ($(n < e, `Invalid index ${n}`); r.size - 1 < n; ) {
        const i = r.size;
        let f;
        switch (typeof s) {
          case "function": {
            f = s(i, t);
            break;
          }
          case "number": {
            f = s;
            break;
          }
        }
        if (i === 0)
          r.set(i, {
            size: f,
            scrollOffset: 0
          });
        else {
          const l = r.get(i - 1);
          $(
            l !== void 0,
            `Unexpected bounds cache miss for index ${n}`
          ), r.set(i, {
            scrollOffset: l.scrollOffset + l.size,
            size: f
          });
        }
      }
      const o = r.get(n);
      return $(
        o !== void 0,
        `Unexpected bounds cache miss for index ${n}`
      ), o;
    },
    set(n, o) {
      r.set(n, o);
    },
    get size() {
      return r.size;
    }
  };
}
function Oe({
  itemCount: e,
  itemProps: t,
  itemSize: s
}) {
  return (0, import_react.useMemo)(
    () => me({
      itemCount: e,
      itemProps: t,
      itemSize: s
    }),
    [e, t, s]
  );
}
function ye({
  containerSize: e,
  itemSize: t
}) {
  let s;
  switch (typeof t) {
    case "string": {
      $(
        t.endsWith("%"),
        `Invalid item size: "${t}"; string values must be percentages (e.g. "100%")`
      ), $(
        e !== void 0,
        "Container size must be defined if a percentage item size is specified"
      ), s = e * parseInt(t) / 100;
      break;
    }
    default: {
      s = t;
      break;
    }
  }
  return s;
}
function ee({
  containerElement: e,
  containerStyle: t,
  defaultContainerSize: s = 0,
  direction: r,
  isRtl: n = false,
  itemCount: o,
  itemProps: i,
  itemSize: f,
  onResize: l,
  overscanCount: c
}) {
  const [d, a] = (0, import_react.useState)({
    startIndexVisible: 0,
    startIndexOverscan: 0,
    stopIndexVisible: -1,
    stopIndexOverscan: -1
  }), {
    startIndexVisible: u,
    startIndexOverscan: I,
    stopIndexVisible: m,
    stopIndexOverscan: b
  } = {
    startIndexVisible: Math.min(o - 1, d.startIndexVisible),
    startIndexOverscan: Math.min(o - 1, d.startIndexOverscan),
    stopIndexVisible: Math.min(o - 1, d.stopIndexVisible),
    stopIndexOverscan: Math.min(o - 1, d.stopIndexOverscan)
  }, { height: g = s, width: w = s } = Ie({
    defaultHeight: r === "vertical" ? s : void 0,
    defaultWidth: r === "horizontal" ? s : void 0,
    element: e,
    mode: r === "vertical" ? "only-height" : "only-width",
    style: t
  }), y = (0, import_react.useRef)({
    height: 0,
    width: 0
  }), V = r === "vertical" ? g : w, h = ye({ containerSize: V, itemSize: f });
  (0, import_react.useLayoutEffect)(() => {
    if (typeof l == "function") {
      const p = y.current;
      (p.height !== g || p.width !== w) && (l({ height: g, width: w }, { ...p }), p.height = g, p.width = w);
    }
  }, [g, l, w]);
  const z = Oe({
    itemCount: o,
    itemProps: i,
    itemSize: h
  }), k = (0, import_react.useCallback)(
    (p) => z.get(p),
    [z]
  ), S = (0, import_react.useCallback)(
    () => fe({
      cachedBounds: z,
      itemCount: o,
      itemSize: h
    }),
    [z, o, h]
  ), W = (0, import_react.useCallback)(
    (p) => {
      const T = Z({
        containerElement: e,
        direction: r,
        isRtl: n,
        scrollOffset: p
      });
      return ie({
        cachedBounds: z,
        containerScrollOffset: T,
        containerSize: V,
        itemCount: o,
        overscanCount: c
      });
    },
    [
      z,
      e,
      V,
      r,
      n,
      o,
      c
    ]
  );
  q(() => {
    const p = (r === "vertical" ? e == null ? void 0 : e.scrollTop : e == null ? void 0 : e.scrollLeft) ?? 0;
    a(W(p));
  }, [e, r, W]), q(() => {
    if (!e)
      return;
    const p = () => {
      a((T) => {
        const { scrollLeft: R, scrollTop: v } = e, x = Z({
          containerElement: e,
          direction: r,
          isRtl: n,
          scrollOffset: r === "vertical" ? v : R
        }), A = ie({
          cachedBounds: z,
          containerScrollOffset: x,
          containerSize: V,
          itemCount: o,
          overscanCount: c
        });
        return Y(A, T) ? T : A;
      });
    };
    return e.addEventListener("scroll", p), () => {
      e.removeEventListener("scroll", p);
    };
  }, [
    z,
    e,
    V,
    r,
    o,
    c
  ]);
  const O = ae(
    ({
      align: p = "auto",
      containerScrollOffset: T,
      index: R
    }) => {
      let v = we({
        align: p,
        cachedBounds: z,
        containerScrollOffset: T,
        containerSize: V,
        index: R,
        itemCount: o,
        itemSize: h
      });
      if (e) {
        if (v = Z({
          containerElement: e,
          direction: r,
          isRtl: n,
          scrollOffset: v
        }), typeof e.scrollTo != "function") {
          const x = W(v);
          Y(d, x) || a(x);
        }
        return v;
      }
    }
  );
  return {
    getCellBounds: k,
    getEstimatedSize: S,
    scrollToIndex: O,
    startIndexOverscan: I,
    startIndexVisible: u,
    stopIndexOverscan: b,
    stopIndexVisible: m
  };
}
function de(e) {
  return (0, import_react.useMemo)(() => e, Object.values(e));
}
function ue(e, t) {
  const {
    ariaAttributes: s,
    style: r,
    ...n
  } = e, {
    ariaAttributes: o,
    style: i,
    ...f
  } = t;
  return Y(s, o) && Y(r, i) && Y(n, f);
}
function Ee({
  cellComponent: e,
  cellProps: t,
  children: s,
  className: r,
  columnCount: n,
  columnWidth: o,
  defaultHeight: i = 0,
  defaultWidth: f = 0,
  dir: l,
  gridRef: c,
  onCellsRendered: d,
  onResize: a,
  overscanCount: u = 3,
  rowCount: I,
  rowHeight: m,
  style: b,
  tagName: g = "div",
  ...w
}) {
  const y = de(t), V = (0, import_react.useMemo)(
    () => (0, import_react.memo)(e, ue),
    [e]
  ), [h, z] = (0, import_react.useState)(null), k = ve(h, l), {
    getCellBounds: S,
    getEstimatedSize: W,
    startIndexOverscan: O,
    startIndexVisible: p,
    scrollToIndex: T,
    stopIndexOverscan: R,
    stopIndexVisible: v
  } = ee({
    containerElement: h,
    containerStyle: b,
    defaultContainerSize: f,
    direction: "horizontal",
    isRtl: k,
    itemCount: n,
    itemProps: y,
    itemSize: o,
    onResize: a,
    overscanCount: u
  }), {
    getCellBounds: x,
    getEstimatedSize: A,
    startIndexOverscan: M,
    startIndexVisible: re,
    scrollToIndex: Q,
    stopIndexOverscan: _,
    stopIndexVisible: ne
  } = ee({
    containerElement: h,
    containerStyle: b,
    defaultContainerSize: i,
    direction: "vertical",
    itemCount: I,
    itemProps: y,
    itemSize: m,
    onResize: a,
    overscanCount: u
  });
  (0, import_react.useImperativeHandle)(
    c,
    () => ({
      get element() {
        return h;
      },
      scrollToCell({
        behavior: B = "auto",
        columnAlign: E = "auto",
        columnIndex: j,
        rowAlign: D = "auto",
        rowIndex: G
      }) {
        const N = T({
          align: E,
          containerScrollOffset: (h == null ? void 0 : h.scrollLeft) ?? 0,
          index: j
        }), ge = Q({
          align: D,
          containerScrollOffset: (h == null ? void 0 : h.scrollTop) ?? 0,
          index: G
        });
        typeof (h == null ? void 0 : h.scrollTo) == "function" && h.scrollTo({
          behavior: B,
          left: N,
          top: ge
        });
      },
      scrollToColumn({
        align: B = "auto",
        behavior: E = "auto",
        index: j
      }) {
        const D = T({
          align: B,
          containerScrollOffset: (h == null ? void 0 : h.scrollLeft) ?? 0,
          index: j
        });
        typeof (h == null ? void 0 : h.scrollTo) == "function" && h.scrollTo({
          behavior: E,
          left: D
        });
      },
      scrollToRow({
        align: B = "auto",
        behavior: E = "auto",
        index: j
      }) {
        const D = Q({
          align: B,
          containerScrollOffset: (h == null ? void 0 : h.scrollTop) ?? 0,
          index: j
        });
        typeof (h == null ? void 0 : h.scrollTo) == "function" && h.scrollTo({
          behavior: E,
          top: D
        });
      }
    }),
    [h, T, Q]
  ), (0, import_react.useEffect)(() => {
    O >= 0 && R >= 0 && M >= 0 && _ >= 0 && d && d(
      {
        columnStartIndex: p,
        columnStopIndex: v,
        rowStartIndex: re,
        rowStopIndex: ne
      },
      {
        columnStartIndex: O,
        columnStopIndex: R,
        rowStartIndex: M,
        rowStopIndex: _
      }
    );
  }, [
    d,
    O,
    p,
    R,
    v,
    M,
    re,
    _,
    ne
  ]);
  const he = (0, import_react.useMemo)(() => {
    const B = [];
    if (n > 0 && I > 0)
      for (let E = M; E <= _; E++) {
        const j = x(E), D = [];
        for (let G = O; G <= R; G++) {
          const N = S(G);
          D.push(
            (0, import_react.createElement)(
              V,
              {
                ...y,
                ariaAttributes: {
                  "aria-colindex": G + 1,
                  role: "gridcell"
                },
                columnIndex: G,
                key: G,
                rowIndex: E,
                style: {
                  position: "absolute",
                  left: k ? void 0 : 0,
                  right: k ? 0 : void 0,
                  transform: `translate(${k ? -N.scrollOffset : N.scrollOffset}px, ${j.scrollOffset}px)`,
                  height: j.size,
                  width: N.size
                }
              }
            )
          );
        }
        B.push(
          (0, import_jsx_runtime.jsx)("div", { role: "row", "aria-rowindex": E + 1, children: D }, E)
        );
      }
    return B;
  }, [
    V,
    y,
    n,
    O,
    R,
    S,
    x,
    k,
    I,
    M,
    _
  ]), pe = (0, import_jsx_runtime.jsx)(
    "div",
    {
      "aria-hidden": true,
      style: {
        height: A(),
        width: W(),
        zIndex: -1
      }
    }
  );
  return (0, import_react.createElement)(
    g,
    {
      "aria-colcount": n,
      "aria-rowcount": I,
      role: "grid",
      ...w,
      className: r,
      dir: l,
      ref: z,
      style: {
        position: "relative",
        width: "100%",
        height: "100%",
        maxHeight: "100%",
        maxWidth: "100%",
        flexGrow: 1,
        overflow: "auto",
        ...b
      }
    },
    he,
    s,
    pe
  );
}
var Ve = import_react.useState;
var Re = import_react.useRef;
function ze(e) {
  return e != null && typeof e == "object" && "getAverageRowHeight" in e && typeof e.getAverageRowHeight == "function";
}
var te = "data-react-window-index";
function Ae({
  children: e,
  className: t,
  defaultHeight: s = 0,
  listRef: r,
  onResize: n,
  onRowsRendered: o,
  overscanCount: i = 3,
  rowComponent: f,
  rowCount: l,
  rowHeight: c,
  rowProps: d,
  tagName: a = "div",
  style: u,
  ...I
}) {
  const m = de(d), b = (0, import_react.useMemo)(
    () => (0, import_react.memo)(f, ue),
    [f]
  ), [g, w] = (0, import_react.useState)(null), y = ze(c), V = (0, import_react.useMemo)(() => y ? (v) => c.getRowHeight(v) ?? c.getAverageRowHeight() : c, [y, c]), {
    getCellBounds: h,
    getEstimatedSize: z,
    scrollToIndex: k,
    startIndexOverscan: S,
    startIndexVisible: W,
    stopIndexOverscan: O,
    stopIndexVisible: p
  } = ee({
    containerElement: g,
    containerStyle: u,
    defaultContainerSize: s,
    direction: "vertical",
    itemCount: l,
    itemProps: m,
    itemSize: V,
    onResize: n,
    overscanCount: i
  });
  (0, import_react.useImperativeHandle)(
    r,
    () => ({
      get element() {
        return g;
      },
      scrollToRow({
        align: v = "auto",
        behavior: x = "auto",
        index: A
      }) {
        const M = k({
          align: v,
          containerScrollOffset: (g == null ? void 0 : g.scrollTop) ?? 0,
          index: A
        });
        typeof (g == null ? void 0 : g.scrollTo) == "function" && g.scrollTo({
          behavior: x,
          top: M
        });
      }
    }),
    [g, k]
  ), q(() => {
    if (!g)
      return;
    const v = Array.from(g.children).filter((x, A) => {
      if (x.hasAttribute("aria-hidden"))
        return false;
      const M = `${S + A}`;
      return x.setAttribute(te, M), true;
    });
    if (y)
      return c.observeRowElements(v);
  }, [
    g,
    y,
    c,
    S,
    O
  ]), (0, import_react.useEffect)(() => {
    S >= 0 && O >= 0 && o && o(
      {
        startIndex: W,
        stopIndex: p
      },
      {
        startIndex: S,
        stopIndex: O
      }
    );
  }, [
    o,
    S,
    W,
    O,
    p
  ]);
  const T = (0, import_react.useMemo)(() => {
    const v = [];
    if (l > 0)
      for (let x = S; x <= O; x++) {
        const A = h(x);
        v.push(
          (0, import_react.createElement)(
            b,
            {
              ...m,
              ariaAttributes: {
                "aria-posinset": x + 1,
                "aria-setsize": l,
                role: "listitem"
              },
              key: x,
              index: x,
              style: {
                position: "absolute",
                left: 0,
                transform: `translateY(${A.scrollOffset}px)`,
                // In case of dynamic row heights, don't specify a height style
                // otherwise a default/estimated height would mask the actual height
                height: y ? void 0 : A.size,
                width: "100%"
              }
            }
          )
        );
      }
    return v;
  }, [
    b,
    h,
    y,
    l,
    m,
    S,
    O
  ]), R = (0, import_jsx_runtime.jsx)(
    "div",
    {
      "aria-hidden": true,
      style: {
        height: z(),
        width: "100%",
        zIndex: -1
      }
    }
  );
  return (0, import_react.createElement)(
    a,
    {
      role: "list",
      ...I,
      className: t,
      ref: w,
      style: {
        position: "relative",
        maxHeight: "100%",
        flexGrow: 1,
        overflowY: "auto",
        ...u
      }
    },
    T,
    e,
    R
  );
}
function ke({
  defaultRowHeight: e,
  key: t
}) {
  const [s, r] = (0, import_react.useState)({
    key: t,
    map: /* @__PURE__ */ new Map()
  });
  s.key !== t && r({
    key: t,
    map: /* @__PURE__ */ new Map()
  });
  const { map: n } = s, o = (0, import_react.useCallback)(() => {
    let a = 0;
    return n.forEach((u) => {
      a += u;
    }), a === 0 ? e : a / n.size;
  }, [e, n]), i = (0, import_react.useCallback)(
    (a) => {
      const u = n.get(a);
      return u !== void 0 ? u : (n.set(a, e), e);
    },
    [e, n]
  ), f = (0, import_react.useCallback)((a, u) => {
    r((I) => {
      if (I.map.get(a) === u)
        return I;
      const m = new Map(I.map);
      return m.set(a, u), {
        ...I,
        map: m
      };
    });
  }, []), l = ae(
    (a) => {
      a.length !== 0 && a.forEach((u) => {
        const { borderBoxSize: I, target: m } = u, b = m.getAttribute(te);
        $(
          b !== null,
          `Invalid ${te} attribute value`
        );
        const g = parseInt(b), { blockSize: w } = I[0];
        w && f(g, w);
      });
    }
  ), [c] = (0, import_react.useState)(
    () => new ResizeObserver(l)
  );
  (0, import_react.useEffect)(() => () => {
    c.disconnect();
  }, [c]);
  const d = (0, import_react.useCallback)(
    (a) => (a.forEach((u) => c.observe(u)), () => {
      a.forEach((u) => c.unobserve(u));
    }),
    [c]
  );
  return (0, import_react.useMemo)(
    () => ({
      getAverageRowHeight: o,
      getRowHeight: i,
      setRowHeight: f,
      observeRowElements: d
    }),
    [o, i, f, d]
  );
}
var Le = import_react.useState;
var Me = import_react.useRef;
var C = -1;
function $e(e = false) {
  if (C === -1 || e) {
    const t = document.createElement("div"), s = t.style;
    s.width = "50px", s.height = "50px", s.overflow = "scroll", document.body.appendChild(t), C = t.offsetWidth - t.clientWidth, document.body.removeChild(t);
  }
  return C;
}
export {
  Ee as Grid,
  Ae as List,
  $e as getScrollbarSize,
  ke as useDynamicRowHeight,
  Ve as useGridCallbackRef,
  Re as useGridRef,
  Le as useListCallbackRef,
  Me as useListRef
};
/*! Bundled license information:

react/cjs/react-jsx-runtime.development.js:
  (**
   * @license React
   * react-jsx-runtime.development.js
   *
   * Copyright (c) Facebook, Inc. and its affiliates.
   *
   * This source code is licensed under the MIT license found in the
   * LICENSE file in the root directory of this source tree.
   *)
*/
//# sourceMappingURL=react-window.js.map
